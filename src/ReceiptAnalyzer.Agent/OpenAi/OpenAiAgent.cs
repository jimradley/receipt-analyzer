using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReceiptAnalyzer.Agent.OpenAi;

public sealed class OpenAiAgent : IAnalysisAgent
{
    private static readonly Uri ChatUrl = new("https://api.openai.com/v1/chat/completions");
    private static readonly Uri ResponsesUrl = new("https://api.openai.com/v1/responses");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly ILogger<OpenAiAgent> _logger;
    private readonly Lazy<string> _rules;

    public OpenAiAgent(HttpClient http, IOptions<AgentOptions> options, ILogger<OpenAiAgent> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _rules = new Lazy<string>(LoadRules);

        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvVar)
            ?? throw new InvalidOperationException($"Env var '{_options.ApiKeyEnvVar}' is not set.");

        if (!_http.DefaultRequestHeaders.Contains("Authorization"))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<ReceiptExtraction> ExtractReceiptAsync(byte[] imageBytes, string mediaType, CancellationToken ct)
    {
        var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(imageBytes)}";

        var messages = BuildMessages(
            ExtractionSystemPrompt,
            new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = dataUrl, ["detail"] = "high" }
                },
                new JsonObject { ["type"] = "text", ["text"] = ExtractionUserPrompt }
            });

        var json = await CallAsync(messages, "extract", ct);
        return JsonSerializer.Deserialize<ReceiptExtraction>(json, JsonOptions)
            ?? throw new InvalidOperationException("Vision response could not be parsed as ReceiptExtraction.");
    }

    public async Task<ItemClassifications> ClassifyAsync(IReadOnlyList<RawItem> items, CancellationToken ct)
    {
        var indexed = items.Select((it, idx) => new
        {
            index = idx,
            name = it.Name,
            quantity = it.Quantity,
            unitPrice = it.UnitPrice
        }).ToArray();

        var userText = $"Classify each item. Items as JSON:\n{JsonSerializer.Serialize(indexed, JsonOptions)}\n\nReturn JSON of shape {{\"items\":[...]}} where each entry includes the original index.";

        var messages = BuildMessages(ClassificationSystemPrompt, userText);

        var json = await CallAsync(messages, "classify", ct);
        return JsonSerializer.Deserialize<ItemClassifications>(json, JsonOptions)
            ?? throw new InvalidOperationException("Classification response could not be parsed.");
    }

    private JsonArray BuildMessages(string taskPrompt, object userContent)
    {
        var systemText = string.IsNullOrWhiteSpace(_rules.Value)
            ? taskPrompt
            : _rules.Value + "\n\n" + taskPrompt;

        return new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemText },
            new JsonObject { ["role"] = "user", ["content"] = JsonNode.Parse(JsonSerializer.Serialize(userContent, JsonOptions)) }
        };
    }

    private async Task<string> CallAsync(JsonArray messages, string stage, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = 4096,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = messages
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = JsonContent.Create(body)
        };
        req.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI call failed: {Status} {Body}", resp.StatusCode, raw);
            throw new HttpRequestException($"OpenAI returned {(int)resp.StatusCode}: {raw}");
        }

        var doc = JsonNode.Parse(raw)
            ?? throw new InvalidOperationException("Empty OpenAI response.");

        var text = (string?)doc["choices"]?[0]?["message"]?["content"]
            ?? throw new InvalidOperationException("No message content in OpenAI response.");

        var usage = doc["usage"];
        ReportUsage(stage, (int?)usage?["prompt_tokens"] ?? 0, (int?)usage?["completion_tokens"] ?? 0);

        return text;
    }

    public Task<SeasonalityResult> AssessSeasonalityAsync(IReadOnlyList<ProduceItem> items, int month, CancellationToken ct)
        => throw new NotImplementedException("Seasonality not yet implemented for OpenAI provider.");

    private void ReportUsage(string stage, int input, int output)
    {
        _logger.LogInformation("OpenAI {Stage} usage: input={Input} output={Output}", stage, input, output);
        UsageReporter.Report(new StageUsage(stage, _options.Model, input, output, 0, 0));
    }

    public async Task<PriceCheckResult> PriceCheckAsync(IReadOnlyList<BrandedItemForCheck> items, CancellationToken ct)
    {
        var itemsJson = JsonSerializer.Serialize(
            items.Select(i => new { index = i.Index, name = i.Name, pricePaid = i.PricePaid, storePaid = i.Retailer }),
            JsonOptions);

        var prompt = $$"""
            {{PriceCheckPrompt}}

            Items to price-check:
            {{itemsJson}}

            Return ONLY valid JSON (no markdown fences, no commentary):
            {
              "items": [
                {
                  "index": 0,
                  "bestPrice": 6.75,
                  "bestPriceStore": "Sainsbury's",
                  "saving": 3.75,
                  "notes": null
                }
              ],
              "skippedSummary": "Items not checked: ..."
            }
            Set bestPrice to null if you cannot find a cheaper price or the item is not suitable for price-checking.
            Saving = pricePaid - bestPrice (positive = cheaper elsewhere).
            Include ALL items in the output (even those with bestPrice=null).
            """;

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["tools"] = new JsonArray { new JsonObject { ["type"] = "web_search_preview" } },
            ["input"] = prompt
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, ResponsesUrl)
        {
            Content = JsonContent.Create(body)
        };
        req.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI Responses call failed: {Status} {Body}", resp.StatusCode, raw);
            throw new HttpRequestException($"OpenAI returned {(int)resp.StatusCode}: {raw}");
        }

        var doc = JsonNode.Parse(raw) ?? throw new InvalidOperationException("Empty OpenAI response.");

        var outputText = doc["output"]?.AsArray()
            .Where(o => (string?)o?["type"] == "message")
            .SelectMany(o => o!["content"]?.AsArray() ?? [])
            .Where(c => (string?)c?["type"] == "output_text")
            .Select(c => (string?)c!["text"])
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(outputText))
            throw new InvalidOperationException("No text output in OpenAI Responses response.");

        _logger.LogInformation("OpenAI price check response length: {Len}", outputText.Length);

        var usage = doc["usage"];
        ReportUsage("price-check", (int?)usage?["input_tokens"] ?? 0, (int?)usage?["output_tokens"] ?? 0);

        var json = ExtractJson(outputText);
        var parsed = JsonSerializer.Deserialize<PriceCheckRaw>(json, JsonOptions)
            ?? throw new InvalidOperationException("Cannot parse price check JSON.");

        var resultItems = parsed.Items.Select(r =>
        {
            var source = items.FirstOrDefault(i => i.Index == r.Index);
            return new PriceCheckItem(
                r.Index,
                source?.Name ?? r.Name ?? "",
                source?.PricePaid ?? 0,
                source?.Retailer ?? "",
                r.BestPrice,
                r.BestPriceStore,
                r.Saving,
                r.Notes);
        }).ToList();

        return new PriceCheckResult(resultItems, parsed.SkippedSummary);
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];
        return text.Trim();
    }

    private string LoadRules()
    {
        var path = _options.RulesFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.LogWarning("RulesFilePath not set or missing; using empty rules.");
            return "";
        }
        return File.ReadAllText(path);
    }

    private sealed record PriceCheckItemRaw(
        int Index,
        string? Name,
        decimal? BestPrice,
        string? BestPriceStore,
        decimal? Saving,
        string? Notes
    );

    private sealed record PriceCheckRaw(
        List<PriceCheckItemRaw> Items,
        string? SkippedSummary
    );

    private const string ExtractionSystemPrompt = """
You are extracting structured data from a UK supermarket receipt photo.
Identify the retailer, the receipt date (if printed), each line item, and any printed totals.
Reject the request if the photo is not a receipt.

SECURITY RULE:
- REDACT all PII (Personally Identifiable Information).
- Do NOT extract full credit card numbers, cardholder names, phone numbers, or loyalty account IDs.
- Use the retailer name but REDACT specific store addresses/phone numbers.
- If a line contains sensitive data, omit it or use [REDACTED].

Output ONLY valid JSON. No commentary.
""";

    private const string ExtractionUserPrompt = """
Extract this receipt to JSON of shape:
{
  "retailer": string,
  "receiptDate": string | null,                // ISO yyyy-MM-dd if visible
  "items": [
    { "name": string, "quantity": int, "unitPrice": number, "lineTotal": number }
  ],
  "printedSubtotal": number | null,
  "printedTotal": number | null,
  "savings": number | null,                    // discounts/loyalty if shown
  "isReceipt": bool,
  "confidence": number,                        // 0..1
  "notes": string | null                       // anything unusual (illegible lines, multi-pack ambiguity)
}
If the photo is not a receipt, set isReceipt=false and leave items empty.
Use the printed item names verbatim.
""";

    private const string ClassificationSystemPrompt = """
You classify UK supermarket items by NOVA processing level and brand ownership.
Apply these rules strictly (from the Shopping Agent rules in the system prompt):
- NOVA 1 = unprocessed/minimally processed (fresh produce, raw meat, plain dairy).
- NOVA 2 = culinary ingredients (oils, salt, sugar, herbs/spices).
- NOVA 3 = processed (cheeses, preserved meats, simple breads).
- NOVA 4 = ultra-processed (multiple additives, emulsifiers, refined starches).
- isAmerican = TRUE only if the brand's parent company is headquartered in the USA.
  Strict by ownership, not perception. Example: Schwartz=McCormick (USA, true);
  Pladis brands (McVitie's etc.)=Turkish (false); Maltesers=Mars (USA, true).
- isOwnLabel = TRUE for supermarket own-label (e.g. Morrisons "M ", Waitrose Essential, Tesco own).
- swapSuggestion: only set for NOVA 3/4 items OR American brands; one short sentence.
Output ONLY valid JSON. No commentary.
""";

    private const string PriceCheckPrompt = """
You are a UK supermarket price comparison assistant.
Search for current UK supermarket prices for each branded item provided.
Check major UK supermarkets: Sainsbury's, Asda, Morrisons, Waitrose, Ocado, Aldi, Lidl.
Do NOT use Tesco — it is not near the user; never include Tesco prices or Tesco Clubcard.
Include loyalty card prices where available (Sainsbury's Nectar, Morrisons More).
Use trolley.co.uk as a reference source.
Skip items that are commodity produce, unbranded, or where you cannot find a reliable price.
""";
}
