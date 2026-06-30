using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger.Parsers;

namespace ReceiptAnalyzer.Ledger;

/// <summary>
/// Durable, append-only record of every line item bought, so purchase cadence can be learned over time.
/// Mirrors <see cref="LedgerStore"/>: JSON on the bind-mounted <c>.state</c> volume with atomic writes,
/// and a one-time migration from the existing per-receipt markdown reports when the JSON is absent —
/// without this the feature would be empty until weeks of fresh receipts accrued.
/// </summary>
public sealed class PurchaseHistoryStore
{
    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");
    private static readonly string[] FilenameDateFormats = { "dd-MMMM-yy", "d-MMMM-yy" };
    private static readonly HashSet<string> LedgerFiles =
        new(StringComparer.OrdinalIgnoreCase) { "buy-elsewhere.md", "alternatives.md" };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _outputDir;
    private readonly string _path;
    private readonly ILogger<PurchaseHistoryStore> _logger;
    private readonly object _gate = new();

    public PurchaseHistoryStore(string outputDir, ILogger<PurchaseHistoryStore> logger)
    {
        _outputDir = outputDir;
        _path = PathSanitizer.EnsureSafePath(outputDir, Path.Combine(".state", "purchase-history.json"));
        _logger = logger;
    }

    public PurchaseHistoryData Load()
    {
        lock (_gate)
        {
        if (File.Exists(_path))
        {
            var json = File.ReadAllText(_path);
            return RepairKeys(JsonSerializer.Deserialize<PurchaseHistoryData>(json, JsonOptions) ?? new PurchaseHistoryData());
        }

        _logger.LogInformation("purchase-history.json not found — back-filling from existing reports.");
        return Migrate();
        }
    }

    public void Save(PurchaseHistoryData data)
    {
        lock (_gate)
        {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tmp, _path, true);
        }
    }

    /// <summary>
    /// Records one receipt's items, replacing any rows previously contributed by the same
    /// <paramref name="source"/> (the job id) so re-processing a receipt never double-counts.
    /// <paramref name="classifications"/> (keyed by item index) carry the NOVA level and US-ownership
    /// flag forward so longitudinal habits can be flagged; null leaves those fields unset.
    /// </summary>
    public void AppendReceipt(string source, string retailer, DateOnly date, IReadOnlyList<RawItem> items,
        IReadOnlyList<ItemClassification>? classifications = null)
    {
        lock (_gate)
        {
        var classByIndex = classifications?.ToDictionary(c => c.Index);
        var data = Load();
        data.Records.RemoveAll(r => r.Source == source);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            var c = classByIndex is not null ? classByIndex.GetValueOrDefault(i) : null;
            data.Records.Add(new PurchaseRecord(
                KeyNormaliser.Product(item.Name, c?.CanonicalName), item.Name, retailer, date,
                item.Quantity, item.UnitPrice, source,
                c?.NovaLevel, c?.IsAmerican, c?.IsAmerican == true ? c.ParentCompany : null,
                c?.CanonicalName));
        }
        Save(data);
        }
    }

    private PurchaseHistoryData Migrate()
    {
        var data = new PurchaseHistoryData();
        if (!Directory.Exists(_outputDir)) { Save(data); return data; }

        var receipts = 0;
        foreach (var file in Directory.EnumerateFiles(_outputDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (LedgerFiles.Contains(name)) continue;

            var fallbackDate = ParseFilenameDate(Path.GetFileNameWithoutExtension(file));
            ParsedReceipt? parsed;
            try { parsed = ReceiptReportParser.Parse(File.ReadAllText(file), fallbackDate); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not parse report {File}; skipping.", name); continue; }
            if (parsed is null) continue;

            var source = $"backfill:{name}";
            foreach (var item in parsed.Items)
                data.Records.Add(new PurchaseRecord(
                    KeyNormaliser.Product(item.Name), item.Name, parsed.Retailer, parsed.Date,
                    item.Quantity, item.UnitPrice, source,
                    item.NovaLevel, item.IsAmerican, item.ParentCompany));
            receipts++;
        }

        _logger.LogInformation("Back-filled {Records} item(s) from {Receipts} report(s).", data.Records.Count, receipts);
        Save(data);
        return data;
    }

    private static DateOnly? ParseFilenameDate(string fileNameNoExt)
        => DateTime.TryParseExact(fileNameNoExt, FilenameDateFormats, Uk, DateTimeStyles.None, out var d)
            ? DateOnly.FromDateTime(d)
            : null;

    private static PurchaseHistoryData RepairKeys(PurchaseHistoryData data)
    {
        data.Records = data.Records
            .Select(r => r with { Key = KeyNormaliser.Product(r.Item, r.CanonicalName) })
            .GroupBy(r => new { r.Source, r.Key, r.Retailer, r.Date, r.UnitPrice, r.Quantity })
            .Select(g => g.First())
            .ToList();
        return data;
    }
}
