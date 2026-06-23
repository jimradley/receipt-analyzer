using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReceiptAnalyzer.Agent.Claude;

public sealed class ClaudeAgent : IAnalysisAgent
{
    private const string AnthropicVersion = "2023-06-01";
    private static readonly Uri MessagesUrl = new("https://api.anthropic.com/v1/messages");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly ILogger<ClaudeAgent> _logger;
    private readonly Lazy<string> _rules;
    private readonly Lazy<string> _seasonalityJson;

    public ClaudeAgent(HttpClient http, IOptions<AgentOptions> options, ILogger<ClaudeAgent> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _rules = new Lazy<string>(LoadRules);
        _seasonalityJson = new Lazy<string>(() => EmbeddedResource.Load(
            "ReceiptAnalyzer.Agent.Resources.uk-seasonality.json"));

        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvVar)
            ?? throw new InvalidOperationException($"Env var '{_options.ApiKeyEnvVar}' is not set.");

        if (!_http.DefaultRequestHeaders.Contains("x-api-key"))
        {
            _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
        }
    }

    public async Task<ReceiptExtraction> ExtractReceiptAsync(byte[] imageBytes, string mediaType, CancellationToken ct)
    {
        var systemBlocks = BuildCachedSystem(ExtractionSystemPrompt);

        var userContent = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = mediaType,
                    ["data"] = Convert.ToBase64String(imageBytes)
                }
            },
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = ExtractionUserPrompt
            }
        };

        var json = await CallAsync(systemBlocks, userContent, prefill: "{", maxTokens: 4096, stage: "extract", ct);
        return JsonSerializer.Deserialize<ReceiptExtraction>(json, JsonOptions)
            ?? throw new InvalidOperationException("Vision response could not be parsed as ReceiptExtraction.");
    }

    public async Task<ItemClassifications> ClassifyAsync(IReadOnlyList<RawItem> items, CancellationToken ct)
    {
        var systemBlocks = BuildCachedSystem(ClassificationSystemPrompt);

        var indexed = items.Select((it, idx) => new
        {
            index = idx,
            name = it.Name,
            quantity = it.Quantity,
            unitPrice = it.UnitPrice
        }).ToArray();

        var userContent = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = $"Classify each item. Items as JSON:\n{JsonSerializer.Serialize(indexed, JsonOptions)}\n\nReturn JSON of shape {{\"items\":[...]}} where each entry includes the original index."
            }
        };

        var json = await CallAsync(systemBlocks, userContent, prefill: "{", maxTokens: 4096, stage: "classify", ct);
        return JsonSerializer.Deserialize<ItemClassifications>(json, JsonOptions)
            ?? throw new InvalidOperationException("Classification response could not be parsed.");
    }

    public async Task<PriceCheckResult> PriceCheckAsync(IReadOnlyList<BrandedItemForCheck> items, CancellationToken ct)
    {
        var systemBlocks = BuildCachedSystem(PriceCheckSystemPrompt);

        var itemsJson = JsonSerializer.Serialize(
            items.Select(i => new { index = i.Index, name = i.Name, pricePaid = i.PricePaid, storePaid = i.Retailer }),
            JsonOptions);

        var userContent = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = $$"""
                    Price-check these branded items from a UK supermarket receipt:
                    {{itemsJson}}

                    Return ONLY valid JSON of this exact shape:
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
                    Set bestPrice to null if not found or not worth checking.
                    Saving = pricePaid - bestPrice (positive = cheaper elsewhere).
                    Include ALL items in the output.
                    """
            }
        };

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = 4096,
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = 8
                }
            },
            ["system"] = systemBlocks,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = userContent
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = JsonContent.Create(body)
        };
        req.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Anthropic price check call failed: {Status} {Body}", resp.StatusCode, raw);
            throw new HttpRequestException($"Anthropic returned {(int)resp.StatusCode}: {raw}");
        }

        var doc = JsonNode.Parse(raw) ?? throw new InvalidOperationException("Empty Anthropic response.");

        var text = doc["content"]?.AsArray()
            .Where(b => (string?)b?["type"] == "text")
            .Select(b => (string?)b!["text"])
            .LastOrDefault(); // last text block has the final answer

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No text block in Anthropic price check response.");

        ReportUsage("price-check", doc["usage"]);

        var json = ExtractJson(text);
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

    public async Task<SeasonalityResult> AssessSeasonalityAsync(
        IReadOnlyList<ProduceItem> items, int month, CancellationToken ct)
    {
        var cachedText = _rules.Value + "\n\n## UK Seasonality Reference\n\n" + _seasonalityJson.Value;
        var systemBlocks = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = cachedText,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
            },
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = SeasonalitySystemPrompt
            }
        };

        var monthName = new System.Globalization.DateTimeFormatInfo().GetMonthName(month);
        var itemsJson = JsonSerializer.Serialize(
            items.Select(i => new { index = i.Index, name = i.Name }),
            JsonOptions);

        var userContent = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = $$"""
                    Current month: {{monthName}}

                    Assess these items from a UK supermarket receipt:
                    {{itemsJson}}

                    Return ONLY valid JSON of this exact shape:
                    {
                      "items": [
                        {
                          "index": 0,
                          "name": "Asparagus",
                          "isInSeason": true,
                          "likelyOrigin": null,
                          "ukSeasonMonths": "April–June"
                        }
                      ]
                    }
                    Rules:
                    - Only include items that are fresh produce (fruit, vegetables, fresh herbs). Skip eggs, meat, fish, dairy, canned/frozen goods, non-food, and packaged produce with no seasonal variation.
                    - isInSeason: true if UK-grown and genuinely in season this month per the reference calendar.
                    - likelyOrigin: null when in season; set to the likely country/region when out of UK season.
                    - ukSeasonMonths: the typical UK outdoor season, e.g. "April–June" or "July–September".
                    - For always-imported items (bananas, avocados, etc.) set isInSeason=true and likelyOrigin to their typical country; ukSeasonMonths=null.
                    """
            }
        };

        var json = await CallAsync(systemBlocks, userContent, prefill: "{", maxTokens: 2048, stage: "seasonality", ct);

        var parsed = JsonSerializer.Deserialize<SeasonalityRaw>(json, JsonOptions)
            ?? throw new InvalidOperationException("Cannot parse seasonality JSON.");

        var assessments = parsed.Items.Select(r => new SeasonalityAssessment(
            r.Index,
            r.Name,
            r.IsInSeason,
            r.LikelyOrigin,
            r.UkSeasonMonths)).ToList();

        return new SeasonalityResult(assessments);
    }

    private sealed record SeasonalityItemRaw(
        int Index,
        string Name,
        bool IsInSeason,
        string? LikelyOrigin,
        string? UkSeasonMonths
    );

    private sealed record SeasonalityRaw(List<SeasonalityItemRaw> Items);

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];
        return text.Trim();
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

    private async Task<string> CallAsync(JsonArray systemBlocks, JsonArray userContent, string prefill, int maxTokens, string stage, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = maxTokens,
            ["system"] = systemBlocks,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = userContent
                },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = prefill }
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = JsonContent.Create(body)
        };
        req.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Anthropic call failed: {Status} {Body}", resp.StatusCode, raw);
            throw new HttpRequestException($"Anthropic returned {(int)resp.StatusCode}: {raw}");
        }

        var doc = JsonNode.Parse(raw)
            ?? throw new InvalidOperationException("Empty Anthropic response.");

        var text = doc["content"]?.AsArray()
            .Where(b => (string?)b?["type"] == "text")
            .Select(b => (string?)b!["text"])
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No text block in Anthropic response.");

        ReportUsage(stage, doc["usage"]);

        return prefill + text;
    }

    private JsonArray BuildCachedSystem(string taskPrompt)
    {
        var rules = _rules.Value;
        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = rules,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" }
            },
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = taskPrompt
            }
        };
    }

    private void ReportUsage(string stage, JsonNode? usage)
    {
        if (usage is null) return;
        var input = (int?)usage["input_tokens"] ?? 0;
        var output = (int?)usage["output_tokens"] ?? 0;
        var cacheRead = (int?)usage["cache_read_input_tokens"] ?? 0;
        var cacheCreate = (int?)usage["cache_creation_input_tokens"] ?? 0;
        _logger.LogInformation(
            "Claude {Stage} usage: input={Input} output={Output} cache_read={CacheRead} cache_create={CacheCreate}",
            stage, input, output, cacheRead, cacheCreate);
        UsageReporter.Report(new StageUsage(stage, _options.Model, input, output, cacheRead, cacheCreate));
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
Apply these rules strictly (from the Shopping Agent rules in the cached prefix):
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

    private const string SeasonalitySystemPrompt = """
You assess UK supermarket fresh produce for seasonal availability.
Use the UK seasonality reference calendar in the cached prefix.
Focus only on genuinely fresh produce — skip packaged non-produce items, eggs, fish, meat, and dairy.
Output ONLY valid JSON. No commentary.
""";

    private const string PriceCheckSystemPrompt = """
You are a UK supermarket price comparison assistant.
Search for current UK supermarket prices for each branded item provided.
Check major UK supermarkets: Sainsbury's, Asda, Morrisons, Waitrose, Ocado, Aldi, Lidl.
Do NOT use Tesco — it is not near the user; never include Tesco prices or Tesco Clubcard.
Include loyalty card prices where available (Sainsbury's Nectar, Morrisons More).
Use trolley.co.uk as a reference source.
Skip items that are commodity produce, unbranded, or where you cannot find a reliable price.
Output ONLY valid JSON. No commentary.
""";
}
