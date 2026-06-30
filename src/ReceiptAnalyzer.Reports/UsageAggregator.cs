using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Reports;

/// <summary>Spend over the running periods plus per-model and recent breakdowns. GBP figures are estimates.</summary>
public sealed record UsageSummary(
    decimal TodayGbp,
    decimal MonthGbp,
    decimal AllTimeGbp,
    int ReceiptCount,
    IReadOnlyList<ModelUsageSummary> ByModel,
    IReadOnlyList<RecentUsageSummary> Recent);

public sealed record ModelUsageSummary(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal? Gbp);

public sealed record RecentUsageSummary(DateTimeOffset CompletedAt, string? Retailer, decimal? CostGbp);

/// <summary>
/// Pure aggregation over the usage ledger: running GBP totals (today / this month / all-time, on
/// London-day boundaries), a per-model token+cost breakdown, and the most recent receipts.
/// </summary>
public static class UsageAggregator
{
    public static UsageSummary Summarise(
        IReadOnlyList<UsageLedgerEntry> entries,
        IReadOnlyDictionary<string, ModelPricing> pricing,
        decimal usdToGbp,
        DateTimeOffset nowUtc,
        int recentCount = 20)
    {
        var nowLondon = ToLondon(nowUtc);
        var today = DateOnly.FromDateTime(nowLondon.DateTime);

        decimal todayGbp = 0m, monthGbp = 0m, allTimeGbp = 0m;
        foreach (var e in entries)
        {
            var cost = e.CostGbp ?? 0m;
            allTimeGbp += cost;
            var local = ToLondon(e.CompletedAt).DateTime;
            if (local.Year == nowLondon.Year && local.Month == nowLondon.Month) monthGbp += cost;
            if (DateOnly.FromDateTime(local) == today) todayGbp += cost;
        }

        var byModel = entries
            .SelectMany(e => e.Usage)
            .GroupBy(u => u.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var usd = UsageCost.EstimateUsd(g.ToList(), pricing);
                return new ModelUsageSummary(
                    g.Key,
                    g.Sum(u => (long)u.InputTokens),
                    g.Sum(u => (long)u.OutputTokens),
                    g.Sum(u => (long)u.CacheReadTokens),
                    g.Sum(u => (long)u.CacheCreationTokens),
                    usd is { } v ? Math.Round(v * usdToGbp, 4) : null);
            })
            .OrderByDescending(m => m.Gbp ?? 0m)
            .ToList();

        var recent = entries
            .OrderByDescending(e => e.CompletedAt)
            .Take(recentCount)
            .Select(e => new RecentUsageSummary(e.CompletedAt, e.Retailer, e.CostGbp))
            .ToList();

        return new UsageSummary(
            Math.Round(todayGbp, 4),
            Math.Round(monthGbp, 4),
            Math.Round(allTimeGbp, 4),
            entries.Count,
            byModel,
            recent);
    }

    private static DateTimeOffset ToLondon(DateTimeOffset utc)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
            return TimeZoneInfo.ConvertTime(utc, tz);
        }
        catch (TimeZoneNotFoundException)
        {
            return utc.ToLocalTime();
        }
    }
}
