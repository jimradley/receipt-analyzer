using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Agent;

/// <summary>Authoritative UK produce calendar; model output may supply origin text, not season truth.</summary>
public static class UkSeasonalityCatalog
{
    private sealed record Data(
        Dictionary<string, string[]> InSeason,
        Dictionary<string, string> OriginWhenOutOfSeason);

    private static readonly Lazy<Data> Catalog = new(() =>
        JsonSerializer.Deserialize<Data>(EmbeddedResource.Load(
            "ReceiptAnalyzer.Agent.Resources.uk-seasonality.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("UK seasonality catalogue is invalid."));

    public static bool TryResolve(string itemName, out string produceName)
    {
        var input = Words(itemName);
        var names = Catalog.Value.InSeason.Values.SelectMany(x => x)
            .Concat(Catalog.Value.OriginWhenOutOfSeason.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Length);
        produceName = names.FirstOrDefault(name => ContainsWords(input, Words(name))) ?? string.Empty;
        return produceName.Length > 0;
    }

    public static SeasonalityAssessment Apply(ProduceItem item, int month, SeasonalityAssessment? model = null)
    {
        if (!TryResolve(item.Name, out var resolved))
            throw new ArgumentException("Item is not in the seasonality catalogue.", nameof(item));

        var monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);
        var inSeason = Catalog.Value.InSeason.TryGetValue(monthName, out var names)
            && names.Any(x => Words(x) == Words(resolved));
        var origin = Catalog.Value.OriginWhenOutOfSeason
            .FirstOrDefault(x => Words(x.Key) == Words(resolved)).Value;
        var alwaysImported = origin?.StartsWith("Always imported", StringComparison.OrdinalIgnoreCase) == true;
        var seasonMonths = alwaysImported ? null : FormatMonths(resolved);

        return new SeasonalityAssessment(
            item.Index,
            item.Name,
            inSeason || alwaysImported,
            inSeason ? null : model?.LikelyOrigin ?? origin,
            seasonMonths);
    }

    private static string? FormatMonths(string name)
    {
        var months = Catalog.Value.InSeason
            .Where(x => x.Value.Any(n => Words(n) == Words(name)))
            .Select(x => x.Key[..3])
            .ToList();
        return months.Count == 0 ? null : string.Join(", ", months);
    }

    private static string Words(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z]+", " ").Trim();
    private static bool ContainsWords(string input, string candidate) =>
        (" " + input + " ").Contains(" " + candidate + " ", StringComparison.Ordinal);
}
