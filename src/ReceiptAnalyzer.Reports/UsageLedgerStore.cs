using System.Text.Json;
using System.Text.Json.Serialization;
using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Reports;

/// <summary>One completed analysis's token usage and estimated cost — the durable unit of the cost ledger.</summary>
public sealed record UsageLedgerEntry(
    string JobId,
    DateTimeOffset CompletedAt,
    string? Retailer,
    IReadOnlyList<StageUsage> Usage,
    decimal? CostGbp);

public sealed class UsageLedgerData
{
    public List<UsageLedgerEntry> Entries { get; set; } = new();
}

/// <summary>
/// Append-only, durable record of per-receipt token usage and cost, kept separate from the prunable
/// job records so running totals survive job retention/pruning. JSON on disk at
/// <c>.state/usage-ledger.json</c>; upsert is keyed by job id so re-analysing a receipt never double-counts.
/// </summary>
public sealed class UsageLedgerStore
{
    private readonly string _path;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public UsageLedgerStore(string outputDir)
        => _path = Path.Combine(outputDir, ".state", "usage-ledger.json");

    public UsageLedgerData Load()
    {
        lock (_gate)
        {
        if (!File.Exists(_path)) return new UsageLedgerData();
        return JsonSerializer.Deserialize<UsageLedgerData>(File.ReadAllText(_path), JsonOptions)
            ?? new UsageLedgerData();
        }
    }

    public void Save(UsageLedgerData data)
    {
        lock (_gate)
        {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tmp, _path, true);
        }
    }

    /// <summary>Adds or replaces the entry for this job id (re-analysis replaces, so totals never double-count).</summary>
    public void Upsert(UsageLedgerEntry entry)
    {
        lock (_gate)
        {
        var data = Load();
        data.Entries.RemoveAll(e => e.JobId == entry.JobId);
        data.Entries.Add(entry);
        Save(data);
        }
    }

    public IReadOnlyList<UsageLedgerEntry> All() => Load().Entries;
}
