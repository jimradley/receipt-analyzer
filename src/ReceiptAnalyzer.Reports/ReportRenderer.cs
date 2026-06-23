using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Reports;

public static class ReportRenderer
{
    public static string Render(AnalysisResult result)
    {
        var sb = new StringBuilder();
        var ext = result.Extraction;
        var classByIndex = result.Classifications.Items.ToDictionary(c => c.Index);

        var displayDate = ext.ReceiptDate?.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))
            ?? result.GeneratedAt.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));

        sb.AppendLine($"# Shopping Report — {displayDate}");
        sb.AppendLine();
        sb.Append($"## Receipt: {ext.Retailer} | {displayDate}");
        if (ext.PrintedTotal is { } paid)
        {
            sb.Append($" | Paid: £{paid:F2}");
            if (ext.Savings is { } sav && sav > 0)
                sb.Append($" (after £{sav:F2} savings");
            if (ext.PrintedSubtotal is { } sub && ext.Savings is { } && sub != paid)
                sb.Append($"; balance before deductions £{sub:F2})");
            else if (ext.Savings is { } && ext.Savings > 0)
                sb.Append(')');
        }
        sb.AppendLine();
        sb.AppendLine();

        var math = ReceiptMath.Check(ext);
        var mathIcon = math.Reconciles switch { true => "✅", false => "⚠️", null => "ℹ️" };
        sb.AppendLine($"**Receipt math:** {mathIcon} {math.Summary}");
        sb.AppendLine();

        sb.AppendLine("### Items");
        sb.AppendLine();
        sb.AppendLine("| Item | Qty | Unit | NOVA | 🇺🇸 | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|");
        for (var i = 0; i < ext.Items.Count; i++)
        {
            var item = ext.Items[i];
            classByIndex.TryGetValue(i, out var c);
            var qty = item.Quantity == Math.Floor(item.Quantity) ? ((int)item.Quantity).ToString() : item.Quantity.ToString("G");
            sb.AppendLine($"| {item.Name} | {qty} | £{item.UnitPrice:F2} | {RenderNova(c)} | {RenderUsFlag(c)} | {RenderItemNotes(c)} |");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        var americans = ext.Items
            .Select((it, idx) => (it, c: classByIndex.GetValueOrDefault(idx)))
            .Where(t => t.c is { IsAmerican: true })
            .ToList();
        sb.AppendLine("### American Brand Flags");
        sb.AppendLine();
        if (americans.Count == 0)
        {
            sb.AppendLine("_None on this receipt._");
        }
        else
        {
            foreach (var (it, c) in americans)
            {
                var parent = c!.ParentCompany ?? "Unknown parent";
                var country = c.ParentCountry ?? "USA";
                sb.AppendLine($"**{it.Name}** — {parent} ({country}) 🇺🇸");
                if (!string.IsNullOrWhiteSpace(c.SwapSuggestion))
                    sb.AppendLine($"→ Swap: {c.SwapSuggestion}");
                sb.AppendLine();
            }
        }
        sb.AppendLine("---");
        sb.AppendLine();

        var processed = ext.Items
            .Select((it, idx) => (it, c: classByIndex.GetValueOrDefault(idx)))
            .Where(t => t.c is { NovaLevel: >= 3 })
            .ToList();
        sb.AppendLine("### NOVA 3/4 Items — Inline Swap Suggestions");
        sb.AppendLine();
        if (processed.Count == 0)
        {
            sb.AppendLine("_None on this receipt._");
        }
        else
        {
            foreach (var (it, c) in processed)
            {
                sb.AppendLine($"**{it.Name} (NOVA {c!.NovaLevel})**");
                if (!string.IsNullOrWhiteSpace(c.SwapSuggestion))
                    sb.AppendLine($"→ Swap: {c.SwapSuggestion}");
                sb.AppendLine();
            }
        }
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("### Price Checks (branded items only; threshold £0.30 saving)");
        sb.AppendLine();
        RenderPriceChecks(sb, result.PriceChecks);
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("### Seasonality");
        sb.AppendLine();
        RenderSeasonality(sb, result.Seasonality);

        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("### Notes");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(ext.Notes))
            sb.AppendLine($"- {ext.Notes}");
        sb.AppendLine($"- Generated {result.GeneratedAt.ToLocalTime():dd MMM yyyy HH:mm zzz} by Receipt Analyzer.");
        RenderUsage(sb, result);

        return sb.ToString();
    }

    private static void RenderUsage(StringBuilder sb, AnalysisResult result)
    {
        if (result.Usage is not { Count: > 0 } usage) return;

        var inTok = usage.Sum(u => u.InputTokens + u.CacheReadTokens + u.CacheCreationTokens);
        var outTok = usage.Sum(u => u.OutputTokens);
        var model = usage[0].Model;
        var cost = result.EstimatedCostGbp is { } c ? $" (~£{c:F4} est.)" : "";
        sb.AppendLine($"- Token usage: {inTok:N0} in / {outTok:N0} out across {usage.Count} call(s){cost} [{model}]");
    }

    private static void RenderSeasonality(StringBuilder sb, SeasonalityResult? seasonality)
    {
        if (seasonality is null || seasonality.Items.Count == 0)
        {
            sb.AppendLine("_No fresh produce identified on this receipt._");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            return;
        }

        var outOfSeason = seasonality.Items.Where(s => !s.IsInSeason && s.LikelyOrigin is not null).ToList();
        var inSeason = seasonality.Items.Where(s => s.IsInSeason).ToList();

        if (outOfSeason.Count > 0)
        {
            sb.AppendLine("| Item | UK Season | Likely Origin |");
            sb.AppendLine("|---|---|---|");
            foreach (var s in outOfSeason)
            {
                var season = s.UkSeasonMonths is not null ? $"🌍 UK: {s.UkSeasonMonths}" : "🌍 Not UK-grown";
                sb.AppendLine($"| {s.Name} | {season} | {s.LikelyOrigin} |");
            }
            sb.AppendLine();
        }

        if (inSeason.Count > 0)
        {
            var inSeasonNames = string.Join(", ", inSeason.Select(s =>
                s.LikelyOrigin is not null ? $"{s.Name} ({s.LikelyOrigin})" : $"✅ {s.Name}"));
            sb.AppendLine($"**In season / always imported:** {inSeasonNames}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
    }

    private static void RenderPriceChecks(StringBuilder sb, PriceCheckResult? priceChecks)
    {
        if (priceChecks is null)
        {
            sb.AppendLine("_Price check not available for this receipt._");
            sb.AppendLine();
            return;
        }

        var checkable = priceChecks.Items
            .Where(p => p.BestPrice.HasValue && p.Saving.HasValue)
            .OrderByDescending(p => p.Saving)
            .ToList();

        if (checkable.Count == 0)
        {
            sb.AppendLine("_No branded items found with a cheaper alternative this trip._");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("| Item | Paid | Cheapest | At | Saving |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var p in checkable)
            {
                var savingBold = p.Saving >= 0.30m ? $"**£{p.Saving:F2}**" : $"£{p.Saving:F2}";
                sb.AppendLine($"| {p.Name} | £{p.PricePaid:F2} | £{p.BestPrice:F2} | {p.BestPriceStore} | {savingBold} |");
            }
            sb.AppendLine();

            var totalSaving = checkable.Where(p => p.Saving >= 0.30m).Sum(p => p.Saving!.Value);
            if (totalSaving > 0)
                sb.AppendLine($"**Combined potential saving (this trip, branded items only): ~£{totalSaving:F2}**");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(priceChecks.SkippedSummary))
        {
            sb.AppendLine(priceChecks.SkippedSummary);
            sb.AppendLine();
        }
    }

    private static string RenderNova(ItemClassification? c)
    {
        if (c?.NovaLevel is null) return "—";
        var level = c.NovaLevel.Value;
        return level >= 3 ? $"**{level}** 🚩" : level.ToString();
    }

    private static string RenderUsFlag(ItemClassification? c)
    {
        if (c is null || !c.IsAmerican) return "";
        var short_ = string.IsNullOrWhiteSpace(c.ParentCompany) ? "USA" : c.ParentCompany;
        return $"**🇺🇸 {short_}**";
    }

    private static string RenderItemNotes(ItemClassification? c)
    {
        if (c is null) return "";
        if (c.IsOwnLabel) return "Own-label";
        if (!string.IsNullOrWhiteSpace(c.Notes)) return c.Notes!;
        if (c.NovaLevel is >= 3) return "See swap below";
        return "";
    }
}
