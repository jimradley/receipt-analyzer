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
    private readonly Lazy<string> _seasonalityJson;

    public OpenAiAgent(HttpClient http, IOptions<AgentOptions> options, ILogger<OpenAiAgent> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _rules = new Lazy<string>(LoadRules);
        _seasonalityJson = new Lazy<string>(() => EmbeddedResource.Load(
            "ReceiptAnalyzer.Agent.Resources.uk-seasonality.json"));

        var apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvVar)
            ?? throw new InvalidOperationException($"Env var '{_options.ApiKeyEnvVar}' is not set.");

        if (!_http.DefaultRequestHeaders.Contains("Authorization"))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<ReceiptExtraction> ExtractReceiptAsync(byte[] imageBytes, string mediaType, CancellationToken ct, string? correctionHint = null)
    {
        var dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(imageBytes)}";

        var promptText = string.IsNullOrWhiteSpace(correctionHint)
            ? ExtractionUserPrompt
            : ExtractionUserPrompt + "\n\nIMPORTANT: " + correctionHint;

        var messages = BuildMessages(
            ExtractionSystemPrompt,
            new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = dataUrl, ["detail"] = "high" }
                },
                new JsonObject { ["type"] = "text", ["text"] = promptText }
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

        var userText = $$"""
            Classify each item. Items as JSON:
            {{JsonSerializer.Serialize(indexed, JsonOptions)}}

            Return ONLY valid JSON of this EXACT shape (one entry per input item, preserving its index):
            {
              "items": [
                {
                  "index": 0,
                  "canonicalName": "Belvoir Homemade Lemonade Ginger",
                  "novaLevel": 1,
                  "isAmerican": false,
                  "parentCompany": null,
                  "parentCountry": null,
                  "isOwnLabel": false,
                  "swapSuggestion": null,
                  "notes": null
                }
              ]
            }
            Field rules:
            - Include EVERY input item exactly once, keeping its original index.
            - novaLevel: ALWAYS set 1-4 for any food or drink (best-effort, never null for edibles); use null only for non-food (household, toiletries).
            - canonicalName: expand the abbreviated/garbled receipt name into the full real product name (brand + product, and size if obvious) so it can be searched online — e.g. "BELVOIR HOM LEM GING" → "Belvoir Homemade Lemonade Ginger", "BEAVERTON NECK OIL" → "Beavertown Neck Oil IPA". Correct obvious mis-spellings. If genuinely unidentifiable, repeat the original name.
            - isOwnLabel, isAmerican, parentCompany, parentCountry, swapSuggestion: per the rules in the system prompt.
            """;

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

    public async Task<SeasonalityResult> AssessSeasonalityAsync(IReadOnlyList<ProduceItem> items, int month, CancellationToken ct)
    {
        // The UK seasonality calendar goes into the system prompt (OpenAI has no cached prefix).
        var taskPrompt = SeasonalitySystemPrompt + "\n\n## UK Seasonality Reference\n\n" + _seasonalityJson.Value;

        var monthName = new System.Globalization.DateTimeFormatInfo().GetMonthName(month);
        var itemsJson = JsonSerializer.Serialize(
            items.Select(i => new { index = i.Index, name = i.Name }), JsonOptions);

        var userText = $$"""
            Current month: {{monthName}}

            Assess these items from a UK supermarket receipt:
            {{itemsJson}}

            Return ONLY valid JSON of this exact shape:
            {
              "items": [
                { "index": 0, "name": "Asparagus", "isInSeason": true, "likelyOrigin": null, "ukSeasonMonths": "April–June" }
              ]
            }
            Rules:
            - Only include items that are fresh produce (fruit, vegetables, fresh herbs). Skip eggs, meat, fish, dairy, canned/frozen goods, non-food, and packaged produce with no seasonal variation.
            - isInSeason: true if UK-grown and genuinely in season this month per the reference calendar.
            - likelyOrigin: null when in season; set to the likely country/region when out of UK season.
            - ukSeasonMonths: the typical UK outdoor season, e.g. "April–June" or "July–September".
            - For always-imported items (bananas, avocados, etc.) set isInSeason=true and likelyOrigin to their typical country; ukSeasonMonths=null.
            """;

        var messages = BuildMessages(taskPrompt, userText);
        var json = await CallAsync(messages, "seasonality", ct);

        var parsed = JsonSerializer.Deserialize<SeasonalityRaw>(json, JsonOptions)
            ?? throw new InvalidOperationException("Cannot parse seasonality JSON.");

        var assessments = parsed.Items.Select(r => new SeasonalityAssessment(
            r.Index, r.Name, r.IsInSeason, r.LikelyOrigin, r.UkSeasonMonths)).ToList();

        return new SeasonalityResult(assessments);
    }

    private const string SeasonalitySystemPrompt = """
You assess UK supermarket fresh produce for seasonal availability.
Use the UK seasonality reference calendar provided below.
Focus only on genuinely fresh produce — skip packaged non-produce items, eggs, fish, meat, and dairy.
Output ONLY valid JSON. No commentary.
""";

    private sealed record SeasonalityItemRaw(
        int Index,
        string Name,
        bool IsInSeason,
        string? LikelyOrigin,
        string? UkSeasonMonths
    );

    private sealed record SeasonalityRaw(List<SeasonalityItemRaw> Items);

    private void ReportUsage(string stage, int input, int output, string? model = null)
    {
        _logger.LogInformation("OpenAI {Stage} usage: input={Input} output={Output}", stage, input, output);
        UsageReporter.Report(new StageUsage(stage, model ?? _options.Model, input, output, 0, 0));
    }

    public async Task<PriceCheckResult> PriceCheckAsync(IReadOnlyList<BrandedItemForCheck> items, CancellationToken ct, string? hint = null)
    {
        var model = _options.PriceCheckModel ?? _options.Model;

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
                  "found": true,
                  "bestPrice": 6.75,
                  "bestPriceStore": "Sainsbury's",
                  "notes": "250g pack, matches size bought"
                }
              ],
              "skippedSummary": null
            }
            Field rules:
            - found=true with bestPrice + bestPriceStore: the LOWEST current price you found at the allowed stores,
              EVEN IF it is not cheaper than pricePaid. Never omit a price just because it isn't a saving.
            - found=false (bestPrice null) ONLY after genuinely searching and failing to establish a price,
              or when the item is an unbranded loose commodity / own-label with no cross-retailer equivalent.
            - Compare like-for-like pack sizes; if you can only price a different size, still report it and say so in notes.
            - Include ALL items in the output exactly once, keeping their original index.
            """;

        if (!string.IsNullOrWhiteSpace(hint))
            prompt += "\n\nIMPORTANT: " + hint;

        // gpt-5-family models use the GA "web_search" tool on the Responses API;
        // older models (gpt-4o) only support the legacy "web_search_preview" variant.
        var toolType = model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            ? "web_search"
            : "web_search_preview";

        var body = new JsonObject
        {
            ["model"] = model,
            ["tools"] = new JsonArray { new JsonObject { ["type"] = toolType } },
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
        ReportUsage("price-check", (int?)usage?["input_tokens"] ?? 0, (int?)usage?["output_tokens"] ?? 0, model);

        var json = ExtractJson(outputText);
        var parsed = JsonSerializer.Deserialize<PriceCheckRaw>(json, JsonOptions)
            ?? throw new InvalidOperationException("Cannot parse price check JSON.");

        var resultItems = parsed.Items.Select(r =>
        {
            var source = items.FirstOrDefault(i => i.Index == r.Index);
            var notFound = r.Found == false || r.BestPrice is null;
            return new PriceCheckItem(
                r.Index,
                source?.Name ?? r.Name ?? "",
                source?.PricePaid ?? 0,
                source?.Retailer ?? "",
                r.BestPrice,
                r.BestPriceStore,
                Saving: null, // recomputed by the validator
                r.Notes,
                Outcome: notFound ? PriceCheckOutcome.NotFound : null,
                Quantity: source?.Quantity ?? 1);
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
        bool? Found,
        decimal? BestPrice,
        string? BestPriceStore,
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

Reading rules:
- ONE receipt only. If the photo shows more than one receipt, pick the single most complete and legible one, extract only its items, and note which you chose in "notes".
- If the image is rotated or sideways, read it in its correct orientation.
- Transcribe EVERY line item, top to bottom — do not skip faint, partially-cut, or multi-buy lines.
- Items only: do NOT include loyalty/points-summary lines (e.g. Nectar/More points), card/payment, change, or the savings line as items.
- If the receipt prints a "number of items" / item count, make your list match it.
- Set "confidence" below 0.5 and explain in "notes" when the photo is blurry, creased, cropped, rotated, or otherwise hard to read.
""";

    private const string ClassificationSystemPrompt = """
You classify UK supermarket items by NOVA processing level and brand ownership.
Apply these rules strictly (from the Shopping Agent rules in the system prompt):
- NOVA 1 = unprocessed/minimally processed (fresh produce, raw meat, plain dairy).
- NOVA 2 = culinary ingredients (oils, salt, sugar, herbs/spices).
- NOVA 3 = processed (cheeses, preserved meats, simple breads).
- NOVA 4 = ultra-processed (multiple additives, emulsifiers, refined starches).
- Alcoholic drinks — wine, beer, cider, prosecco, champagne, spirits — are NOVA 1; they are
  fermented/distilled, NOT ultra-processed. Do NOT classify a plain wine/beer/spirit as NOVA 4.
  Only pre-mixed/RTD cocktails or alcopops with added flavourings/sweeteners are NOVA 4.
- isAmerican = TRUE only if the brand's parent company is headquartered in the USA.
  Strict by ownership, not perception. Example: Schwartz=McCormick (USA, true);
  Pladis brands (McVitie's etc.)=Turkish (false); Maltesers=Mars (USA, true).
- isOwnLabel = TRUE for supermarket own-label. Recognise these receipt abbreviations/prefixes as own-label:
  Waitrose = "WAITROSE","WR","WR ESS","ESS" (Essential),"DUCHY"; Morrisons = "M " prefix,"MORR","THE BEST","M SAVERS";
  Sainsbury's = "BY SAINSBURY'S","JS","TASTE THE DIFFERENCE"; Asda = "ASDA","JUST ESSENTIALS","SMARTPRICE";
  Tesco = "TESCO" (recognise as own-label, but NEVER recommend Tesco); Co-op = "CO OP","COOP". Most Aldi/Lidl items are own-label.
  Also treat a plain generic descriptor with no distinct brand as own-label.
- swapSuggestion: only set for NOVA 3/4 items OR American brands; one short sentence.
Output ONLY valid JSON. No commentary.
""";

    private const string PriceCheckPrompt = """
You are a UK supermarket price comparison assistant.
The item names provided have already been expanded to real product names — search the current price for each at major UK supermarkets: Sainsbury's, Asda, Morrisons, Waitrose, Ocado, Aldi, Lidl.
Do NOT use Tesco — it is not near the user; never include Tesco prices or Tesco Clubcard.
Include loyalty card prices where available (Sainsbury's Nectar, Morrisons More).
Use trolley.co.uk as a reference source.
Make a genuine effort to find EVERY item before giving up — these are branded products that should be findable; try the brand + product name.
Report the LOWEST price you find at the allowed stores for every item, even when it is the same as or higher than what was paid — knowing the price paid was already the best is valuable too.
Only report an item as not found after genuinely searching for it, or when it is truly an unbranded loose commodity (e.g. loose fruit/veg) or supermarket own-label with no cross-retailer equivalent. Do NOT give up on a recognisable brand just because the first search is unclear.
""";
}
