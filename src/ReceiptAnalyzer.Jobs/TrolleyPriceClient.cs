using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Jobs;

public sealed record ProductCandidate(string Name, string Url);

public sealed class TrolleyPriceClient
{
    private const string BaseUrl = "https://www.trolley.co.uk";
    private static readonly HashSet<string> MajorStores = new(StringComparer.OrdinalIgnoreCase)
        { "Tesco", "Asda", "Sainsbury's", "Morrisons", "Waitrose", "Ocado", "Aldi", "Lidl" };
    private static readonly Regex ProductLink = new(
        "<a(?<attrs>[^>]+href=\"(?<url>/product/[^\"]+)\"[^>]*)>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ProductName = new(
        "class=\"_name\"[^>]*>(?<name>.*?)</(?:div|span)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ProductTitle = new(
        "title=\"(?<title>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProductBrand = new(
        "class=\"_brand\"[^>]*>(?<brand>[^<]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OfferBlock = new(
        "<div class=\"_item\">(?<body>.*?)(?=<div class=\"_item\">|</div></div><style|<div class=\"disclaimer|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StoreTitle = new(
        "(?:<svg[^>]+title=\"(?<store>[^\"]+)\"|<title>(?<store2>[^<]+)</title>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Price = new(
        @"<b>\s*(?:&pound;|£)\s*(?<price>\d+(?:\.\d{1,2})?)\s*</b>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MultiBuy = new(
        @"(?:any\s+)?(?<qty>\d+)\s+for\s+(?:£|&pound;)\s*(?<total>\d+(?:\.\d{1,2})?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    public TrolleyPriceClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<ProductCandidate>> SearchAsync(string product, CancellationToken ct)
    {
        var html = await _http.GetStringAsync($"search/?q={Uri.EscapeDataString(product)}", ct);
        return ParseCandidates(html).Take(8).ToList();
    }

    public async Task<IReadOnlyList<StorePriceOffer>> GetOffersAsync(string url, DateOnly today, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("trolley.co.uk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Trolley product URLs may be fetched.");
        var html = await _http.GetStringAsync(uri, ct);
        return ParseOffers(html, uri.ToString(), today);
    }

    public static IReadOnlyList<ProductCandidate> ParseCandidates(string html)
    {
        var candidates = new Dictionary<string, ProductCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ProductLink.Matches(html))
        {
            var relative = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (!relative.StartsWith("/product/", StringComparison.OrdinalIgnoreCase)) continue;
            var body = match.Groups["body"].Value;
            var nameMatch = ProductName.Match(body);
            var titleMatch = ProductTitle.Match(match.Groups["attrs"].Value);
            var brandMatch = ProductBrand.Match(body);
            var title = titleMatch.Success ? titleMatch.Groups["title"].Value
                : nameMatch.Success ? nameMatch.Groups["name"].Value : body;
            var name = Clean((brandMatch.Success ? brandMatch.Groups["brand"].Value + " " : "") + title);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var url = new Uri(new Uri(BaseUrl), relative).ToString();
            candidates.TryAdd(url, new ProductCandidate(name, url));
        }
        return candidates.Values.ToList();
    }

    public static IReadOnlyList<StorePriceOffer> ParseOffers(string html, string sourceUrl, DateOnly today)
    {
        var result = new List<StorePriceOffer>();
        var comparisonStart = html.IndexOf("class=\"comparison-table\"", StringComparison.OrdinalIgnoreCase);
        if (comparisonStart < 0) return result;
        var comparisonEnd = html.IndexOf("class=\"disclaimer price\"", comparisonStart, StringComparison.OrdinalIgnoreCase);
        var section = comparisonEnd > comparisonStart ? html[comparisonStart..comparisonEnd] : html[comparisonStart..];

        foreach (Match block in OfferBlock.Matches(section))
        {
            var body = block.Groups["body"].Value;
            var storeMatch = StoreTitle.Match(body);
            var priceMatch = Price.Match(body);
            if (!storeMatch.Success || !priceMatch.Success) continue;
            var rawStore = storeMatch.Groups["store"].Success
                ? storeMatch.Groups["store"].Value : storeMatch.Groups["store2"].Value;
            var store = StoreCatalog.Canonical(WebUtility.HtmlDecode(rawStore));
            if (store is null || !MajorStores.Contains(store) ||
                !decimal.TryParse(priceMatch.Groups["price"].Value, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var shelfPrice)) continue;

            var text = Clean(body);
            var multi = MultiBuy.Match(text);
            var minimum = 1;
            var checkout = shelfPrice;
            var effective = shelfPrice;
            if (multi.Success && int.TryParse(multi.Groups["qty"].Value, out var quantity) && quantity > 1 &&
                decimal.TryParse(multi.Groups["total"].Value, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var total))
            {
                minimum = quantity;
                checkout = total;
                effective = Math.Round(total / quantity, 2, MidpointRounding.AwayFromZero);
            }

            var loyalty = text.Contains("clubcard", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("nectar", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("more card", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("mywaitrose", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("rewards", StringComparison.OrdinalIgnoreCase);
            var deal = minimum > 1
                ? WebUtility.HtmlDecode(multi.Value) + (loyalty ? "; loyalty price" : "")
                : loyalty ? "Loyalty price" : null;
            result.Add(new StorePriceOffer(store, shelfPrice, effective, minimum, checkout,
                deal, loyalty, today.ToString("yyyy-MM-dd"), sourceUrl));
        }

        return result
            .GroupBy(o => o.Store, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(o => o.EffectivePrice).First())
            .ToList();
    }

    private static string Clean(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(Tags.Replace(value, " ")), @"\s+", " ").Trim();
}
