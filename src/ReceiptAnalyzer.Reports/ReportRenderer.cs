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
        // A large, unreconciled gap usually means the photo had more than one receipt or was rotated —
        // the read can't be trusted, so give actionable advice rather than presenting garbled items as fact.
        if (math.Reconciles == false && IsLargeMismatch(math))
            sb.AppendLine("> ⚠️ **This read looks unreliable** — the photo may contain more than one receipt, be rotated, or be too creased/blurry to read. For an accurate result, re-shoot **one receipt at a time, upright and flattened**.");
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

        sb.AppendLine("### Your Price History (cheaper before)");
        sb.AppendLine();
        RenderPersonalPrices(sb, result.PersonalPrices);
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
        var models = string.Join(", ", usage.Select(u => u.Model).Distinct());
        var cost = result.EstimatedCostGbp is { } c ? $" (~£{c:F4} est.)" : "";
        sb.AppendLine($"- Token usage: {inTok:N0} in / {outTok:N0} out across {usage.Count} stage(s){cost} [{models}]");
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
        if (priceChecks is null || priceChecks.Items.Count == 0)
        {
            sb.AppendLine("_No branded items to price-check on this receipt._");
            sb.AppendLine();
            return;
        }

        var byOutcome = priceChecks.Items.ToLookup(OutcomeOf);
        var cheaper = byOutcome[PriceCheckOutcome.CheaperElsewhere].OrderByDescending(p => p.Saving).ToList();
        var alreadyBest = byOutcome[PriceCheckOutcome.AlreadyBest].ToList();
        var notFound = byOutcome[PriceCheckOutcome.NotFound].ToList();
        var unresolved = byOutcome[PriceCheckOutcome.Unchecked].ToList();

        // Coverage line: every branded item is accounted for, so a skipped or failed check is
        // visible instead of silently vanishing. "Checked" = a search actually happened.
        var total = priceChecks.Items.Count;
        var searched = total - unresolved.Count;
        var segments = new List<string>();
        if (cheaper.Count > 0) segments.Add($"{cheaper.Count} cheaper elsewhere");
        if (alreadyBest.Count > 0) segments.Add($"{alreadyBest.Count} already best price");
        if (notFound.Count > 0) segments.Add($"{notFound.Count} not found");
        if (unresolved.Count > 0) segments.Add($"{unresolved.Count} not checked");
        sb.AppendLine($"Price-checked {searched} of {total} branded item(s): {string.Join(", ", segments)}.");
        sb.AppendLine();

        if (cheaper.Count == 0)
        {
            sb.AppendLine("_No cheaper alternatives found this trip._");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("| Item | Paid | Cheapest | At | Saving |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var p in cheaper)
            {
                var savingBold = p.Saving >= 0.30m ? $"**£{p.Saving:F2}**" : $"£{p.Saving:F2}";
                var paid = p.Quantity > 1 ? $"£{p.PricePaid:F2} ×{p.Quantity:0.##}" : $"£{p.PricePaid:F2}";
                sb.AppendLine($"| {p.Name} | {paid} | £{p.BestPrice:F2} | {p.BestPriceStore} | {savingBold} |");
            }
            sb.AppendLine();

            // Per-trip total: unit saving × quantity, so multi-buys aren't undercounted.
            var totalSaving = cheaper.Where(p => p.Saving >= 0.30m)
                .Sum(p => p.Saving!.Value * (p.Quantity > 0 ? p.Quantity : 1));
            if (totalSaving > 0)
                sb.AppendLine($"**Combined potential saving (this trip, branded items only): ~£{totalSaving:F2}**");
            sb.AppendLine();
        }

        if (alreadyBest.Count > 0)
        {
            sb.AppendLine($"Already the best price: {string.Join(", ", alreadyBest.Select(p => p.Name))}.");
            sb.AppendLine();
        }
        if (notFound.Count > 0)
        {
            sb.AppendLine($"Couldn't price: {string.Join(", ", notFound.Select(p => p.Name))}.");
            sb.AppendLine();
        }
        if (unresolved.Count > 0)
        {
            sb.AppendLine($"Not checked (error — will retry on the next receipt): {string.Join(", ", unresolved.Select(p => p.Name))}.");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(priceChecks.SkippedSummary))
        {
            sb.AppendLine(priceChecks.SkippedSummary);
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Reports rendered from jobs persisted before outcomes existed derive one: a price with a
    /// positive saving was "cheaper elsewhere"; anything else was the old "nothing cheaper" bucket.
    /// </summary>
    private static string OutcomeOf(PriceCheckItem p) =>
        p.Outcome ?? (p.BestPrice.HasValue && p.Saving > 0
            ? PriceCheckOutcome.CheaperElsewhere
            : PriceCheckOutcome.AlreadyBest);

    private static void RenderPersonalPrices(StringBuilder sb, IReadOnlyList<PersonalPriceComparison>? personal)
    {
        if (personal is not { Count: > 0 })
        {
            sb.AppendLine("_Nothing on this receipt that you've bought cheaper before (within the last 6 months)._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("Based on your own past receipts — not market prices:");
        sb.AppendLine();
        sb.AppendLine("| Item | Paid now | You paid | At | When | Difference |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var p in personal)
        {
            var when = p.BestDate.ToString("d MMM yy", CultureInfo.GetCultureInfo("en-GB"));
            sb.AppendLine($"| {p.Item} | £{p.PaidNow:F2} | £{p.BestUnitPrice:F2} | {p.BestRetailer} | {when} | **£{p.Saving:F2}** |");
        }
        sb.AppendLine();

        var total = personal.Sum(p => p.Saving);
        sb.AppendLine($"**You've paid less before on {personal.Count} item(s) — up to ~£{total:F2} dearer this trip at those items' best past prices.**");
        sb.AppendLine();
    }

    /// <summary>
    /// A mismatch big enough to signal a genuinely bad read (multiple receipts blended / unreadable)
    /// rather than a few missed lines or complex multi-save discounts: the items are off by more than
    /// 15% of the printed figure (and at least a couple of pounds, so tiny receipts don't over-trigger).
    /// </summary>
    /// <summary>
    /// Public so callers outside the renderer (the pipeline's ledger/price-cache suppression guard)
    /// can reuse the same "is this read too unreliable to trust" threshold as the report's own
    /// re-shoot warning.
    /// </summary>
    public static bool IsLargeMismatch(ReceiptMathCheck math)
    {
        if (math.Delta is not { } delta || math.Reference is not { } reference || reference <= 0) return false;
        var abs = Math.Abs(delta);
        return abs > 2.00m && abs / reference > 0.15m;
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
