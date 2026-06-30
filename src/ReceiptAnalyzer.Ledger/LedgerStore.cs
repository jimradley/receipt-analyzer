using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger.Parsers;
using ReceiptAnalyzer.Ledger.Renderers;

namespace ReceiptAnalyzer.Ledger;

public sealed class LedgerStore
{
    private readonly string _ledgerPath;
    private readonly string _buyElsewherePath;
    private readonly string _alternativesPath;
    private readonly ILogger<LedgerStore> _logger;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public LedgerStore(string outputDir, ILogger<LedgerStore> logger)
    {
        _ledgerPath = PathSanitizer.EnsureSafePath(outputDir, Path.Combine(".state", "ledger.json"));
        _buyElsewherePath = PathSanitizer.EnsureSafePath(outputDir, "buy-elsewhere.md");
        _alternativesPath = PathSanitizer.EnsureSafePath(outputDir, "alternatives.md");
        _logger = logger;
    }

    public LedgerData Load()
    {
        lock (_gate)
        {
        if (File.Exists(_ledgerPath))
        {
            var json = File.ReadAllText(_ledgerPath);
            return Deduplicate(JsonSerializer.Deserialize<LedgerData>(json, JsonOptions) ?? new LedgerData());
        }

        _logger.LogInformation("ledger.json not found — migrating from existing markdown files.");
        return Migrate();
        }
    }

    public LedgerMergeResult Merge(LedgerData ledger, AnalysisResult result, string today)
    {
        var added = new List<string>();
        var updated = 0;

        foreach (var classification in result.Classifications.Items)
        {
            if (!classification.IsAmerican && classification.NovaLevel is not (3 or 4)) continue;

            var rawItem = result.Extraction.Items[classification.Index];
            var key = KeyNormaliser.Product(rawItem.Name, classification.CanonicalName);

            var idx = ledger.Alternatives.FindIndex(a => a.Key == key);
            if (idx >= 0)
            {
                ledger.Alternatives[idx] = ledger.Alternatives[idx] with { LastSeen = today };
                updated++;
            }
            else
            {
                var reason = BuildReason(classification);
                ledger.Alternatives.Add(new AlternativeEntry(key, rawItem.Name, reason, classification.SwapSuggestion, null, today));
                added.Add(rawItem.Name);
            }
        }

        if (result.PriceChecks is not null)
        {
            foreach (var pc in result.PriceChecks.Items.Where(p => p.BestPrice.HasValue && p.Saving >= 0.30m))
            {
                var key = KeyNormaliser.Product(pc.Name);
                var idx = ledger.BuyElsewhere.FindIndex(b => b.Key == key);
                if (idx >= 0)
                {
                    var existing = ledger.BuyElsewhere[idx];
                    var updated_ = existing with
                    {
                        LastSeen = today,
                        PricePaid = pc.PricePaid,
                        BestPrice = pc.BestPrice!.Value < existing.BestPrice ? pc.BestPrice.Value : existing.BestPrice,
                        Where = pc.BestPrice!.Value < existing.BestPrice ? (pc.BestPriceStore ?? existing.Where) : existing.Where,
                        Saving = pc.PricePaid - Math.Min(existing.BestPrice, pc.BestPrice.Value),
                        LatestBestPrice = pc.BestPrice,
                        LatestBestPriceStore = pc.BestPriceStore
                    };
                    ledger.BuyElsewhere[idx] = updated_;
                    updated++;
                }
                else
                {
                    ledger.BuyElsewhere.Add(new BuyElsewhereEntry(
                        key,
                        pc.Name,
                        pc.StorePaid,
                        pc.PricePaid,
                        pc.BestPrice!.Value,
                        pc.BestPriceStore ?? "Unknown",
                        pc.Saving!.Value,
                        today,
                        pc.BestPrice,
                        pc.BestPriceStore));
                    added.Add(pc.Name);
                }
            }
        }

        _logger.LogInformation("Ledger merge: +{Added} new, {Updated} updated.", added.Count, updated);
        return new LedgerMergeResult(added.Count, updated, added);
    }

    public void Save(LedgerData ledger)
    {
        lock (_gate)
        {
        Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
        var tmp = _ledgerPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ledger, JsonOptions));
        File.Move(tmp, _ledgerPath, true);
        }
    }

    public void ReRenderMarkdown(LedgerData ledger)
    {
        File.WriteAllText(_buyElsewherePath, BuyElsewhereRenderer.Render(ledger));
        File.WriteAllText(_alternativesPath, AlternativesRenderer.Render(ledger));
    }

    private LedgerData Migrate()
    {
        var ledger = new LedgerData();

        if (File.Exists(_buyElsewherePath))
        {
            var (entries, unparseable) = BuyElsewhereMarkdownParser.Parse(File.ReadAllText(_buyElsewherePath));
            ledger.BuyElsewhere.AddRange(entries);
            if (unparseable.Count > 0)
                _logger.LogWarning("buy-elsewhere migration — {Count} unparseable rows: {Rows}", unparseable.Count, string.Join("; ", unparseable));
            _logger.LogInformation("Migrated {Count} buy-elsewhere entries.", entries.Count);
        }

        if (File.Exists(_alternativesPath))
        {
            var (entries, unparseable) = AlternativesMarkdownParser.Parse(File.ReadAllText(_alternativesPath));
            ledger.Alternatives.AddRange(entries);
            if (unparseable.Count > 0)
                _logger.LogWarning("alternatives migration — {Count} unparseable sections: {Rows}", unparseable.Count, string.Join("; ", unparseable));
            _logger.LogInformation("Migrated {Count} alternatives entries.", entries.Count);
        }

        Save(ledger);
        return ledger;
    }

    private static string BuildReason(ItemClassification c)
    {
        var parts = new List<string>();
        if (c.IsAmerican)
            parts.Add($"{(c.ParentCompany is not null ? $"{c.ParentCompany} (USA)" : "USA-owned")} 🇺🇸");
        if (c.NovaLevel is 3 or 4)
            parts.Add($"NOVA {c.NovaLevel}");
        return string.Join(" + ", parts);
    }

    private static LedgerData Deduplicate(LedgerData data)
    {
        data.Alternatives = data.Alternatives
            .GroupBy(x => KeyNormaliser.Product(x.Item), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.LastSeen).First() with { Key = g.Key })
            .ToList();
        data.BuyElsewhere = data.BuyElsewhere
            .GroupBy(x => KeyNormaliser.Product(x.Item), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.LastSeen).First() with { Key = g.Key })
            .ToList();
        return data;
    }
}
