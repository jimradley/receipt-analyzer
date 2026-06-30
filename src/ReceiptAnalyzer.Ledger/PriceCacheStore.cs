using System.Globalization;
using System.Text.Json;

namespace ReceiptAnalyzer.Ledger;

/// <summary>
/// A small, durable cache of price-check results so we don't re-run a web search for an item we
/// already priced recently. Backed by JSON on disk (<c>.state/price-cache.json</c>), matching the
/// <see cref="LedgerStore"/> pattern. Keyed by <see cref="KeyNormaliser"/> so it stays aligned with
/// the ledger's keys.
/// </summary>
public sealed class PriceCacheStore
{
    private readonly string _path;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public PriceCacheStore(string outputDir)
        => _path = PathSanitizer.EnsureSafePath(outputDir, Path.Combine(".state", "price-cache.json"));

    public PriceCacheData Load()
    {
        lock (_gate)
        {
        if (!File.Exists(_path)) return new PriceCacheData();
        return JsonSerializer.Deserialize<PriceCacheData>(File.ReadAllText(_path), JsonOptions)
            ?? new PriceCacheData();
        }
    }

    public void Save(PriceCacheData data)
    {
        lock (_gate)
        {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tmp, _path, true);
        }
    }

    /// <summary>Returns the entry for <paramref name="key"/> if it was checked on or after <paramref name="cutoff"/>.</summary>
    public static bool TryGetFresh(PriceCacheData data, string key, DateOnly cutoff, out PriceCacheEntry? entry)
    {
        var separator = key.LastIndexOf('|');
        var product = separator >= 0 ? key[..separator] : key;
        var pack = separator >= 0 ? key[(separator + 1)..] : "unknown-size";
        entry = data.Entries.FirstOrDefault(e => e.Key == key)
            ?? data.Entries.FirstOrDefault(e =>
                e.ProductKey == product && (e.Pack ?? "unknown-size") == pack)
            // Compatibility with cache rows written before product/pack fields existed.
            ?? data.Entries.FirstOrDefault(e => e.ProductKey is null &&
                KeyNormaliser.Product(e.Key) == product &&
                (KeyNormaliser.Pack(e.Key) ?? "unknown-size") == pack);
        if (entry is null) return false;
        if (!DateOnly.TryParse(entry.CheckedOn, CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkedOn))
        {
            entry = null;
            return false;
        }
        if (checkedOn < cutoff)
        {
            entry = null;
            return false;
        }
        return true;
    }

    /// <summary>Adds or replaces cache entries (replace keyed on <see cref="PriceCacheEntry.Key"/>).</summary>
    public static void Upsert(PriceCacheData data, IEnumerable<PriceCacheEntry> fresh)
    {
        foreach (var e in fresh)
        {
            data.Entries.RemoveAll(x => x.Key == e.Key);
            data.Entries.Add(e);
        }
    }
}
