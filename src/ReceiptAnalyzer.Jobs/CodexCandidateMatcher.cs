using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Jobs;

public sealed record ProductMatchRequest(InventoryProduct Product, IReadOnlyList<ProductCandidate> Candidates);

public interface IProductCandidateMatcher
{
    Task<IReadOnlyDictionary<string, string>> MatchAsync(
        IReadOnlyList<ProductMatchRequest> products, CancellationToken ct);
}

public sealed class CodexCandidateMatcher : IProductCandidateMatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly InventoryRefreshOptions _options;

    public CodexCandidateMatcher(HttpClient http, InventoryRefreshOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<IReadOnlyDictionary<string, string>> MatchAsync(
        IReadOnlyList<ProductMatchRequest> products, CancellationToken ct)
    {
        if (products.Count == 0) return new Dictionary<string, string>();
        var key = Environment.GetEnvironmentVariable(_options.BridgeKeyEnvVar);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(
            $"{_options.BridgeKeyEnvVar} is not configured for the product matcher.");

        var payload = JsonSerializer.Serialize(products.Select(p => new
        {
            key = p.Product.Key,
            purchasedName = p.Product.Name,
            pack = p.Product.Pack,
            candidates = p.Candidates.Select(c => new { c.Name, c.Url })
        }), JsonOptions);
        var prompt = """
            Match each purchased grocery to a candidate only when it is the same product, variant and pack size.
            Do not choose a merely similar own-label or differently-sized product. Return JSON only, as an array
            of {"key":"inventory key","url":"candidate URL or null"}. Use only candidates supplied below.

            """ + payload;

        using var request = new HttpRequestMessage(HttpMethod.Post, "agent/run");
        request.Headers.Add("X-BRIDGE-KEY", key);
        request.Content = JsonContent.Create(new
        {
            prompt,
            provider = "codex",
            model = _options.CodexModel,
            reasoningEffort = "low",
            allowedTools = Array.Empty<string>(),
            timeoutSeconds = 180
        });
        using var response = await _http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        var envelope = JsonSerializer.Deserialize<BridgeResponse>(raw, JsonOptions);
        if (envelope?.IsError != false || string.IsNullOrWhiteSpace(envelope.ResultText))
            throw new InvalidOperationException(envelope?.ResultText ?? "Codex matcher returned no result.");

        var json = ExtractJson(envelope.ResultText);
        var choices = JsonSerializer.Deserialize<List<MatchChoice>>(json, JsonOptions) ?? [];
        var allowed = products.SelectMany(p => p.Candidates).Select(c => c.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return choices
            .Where(c => !string.IsNullOrWhiteSpace(c.Key) && !string.IsNullOrWhiteSpace(c.Url) && allowed.Contains(c.Url!))
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Url!, StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractJson(string value)
    {
        var start = value.IndexOf('[');
        var end = value.LastIndexOf(']');
        if (start < 0 || end < start) throw new JsonException("Matcher response did not contain a JSON array.");
        return value[start..(end + 1)];
    }

    private sealed record BridgeResponse(string? ResultText, bool IsError);
    private sealed record MatchChoice(string Key, string? Url);
}
