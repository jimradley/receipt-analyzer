using System.Text.Json;

namespace ReceiptAnalyzer.Ledger;

/// <summary>Atomic JSON-backed catalogue of every exact product/pack ever purchased.</summary>
public sealed class InventoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly string _jobsDir;
    private readonly object _gate = new();
    private bool _historicalMappingsImported;

    public InventoryStore(string outputDir)
    {
        _path = PathSanitizer.EnsureSafePath(outputDir, Path.Combine(".state", "product-inventory.json"));
        _jobsDir = PathSanitizer.EnsureSafePath(outputDir, Path.Combine(".state", "jobs"));
    }

    public InventoryData Load()
    {
        lock (_gate) return LoadUnsafe();
    }

    public InventoryData Synchronise(PurchaseHistoryData history)
    {
        lock (_gate)
        {
            var data = LoadUnsafe();
            var existing = data.Products.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
            var rebuilt = history.Records
                .GroupBy(ProductKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var latest = group.OrderByDescending(r => r.Date).First();
                    var name = string.IsNullOrWhiteSpace(latest.CanonicalName) ? latest.Item : latest.CanonicalName!;
                    var key = group.Key;
                    var previous = existing.GetValueOrDefault(key);
                    return previous is null
                        ? new InventoryProduct(
                            key, KeyNormaliser.Product(name), KeyNormaliser.Pack(name) ?? KeyNormaliser.Pack(latest.Item),
                            name, group.Count(), latest.Date.ToString("yyyy-MM-dd"), latest.Retailer,
                            latest.UnitPrice, InventoryMatchStatus.Pending)
                        : previous with
                        {
                            ProductKey = KeyNormaliser.Product(name),
                            Pack = KeyNormaliser.Pack(name) ?? KeyNormaliser.Pack(latest.Item),
                            Name = name,
                            PurchaseCount = group.Count(),
                            LastPurchasedOn = latest.Date.ToString("yyyy-MM-dd"),
                            LastPurchasedStore = latest.Retailer,
                            LastPricePaid = latest.UnitPrice
                        };
                })
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            data.Products = rebuilt;
            if (!_historicalMappingsImported)
            {
                ImportHistoricalMappings(data);
                _historicalMappingsImported = true;
            }
            SaveUnsafe(data);
            return Clone(data);
        }
    }

    public T Update<T>(Func<InventoryData, T> update)
    {
        lock (_gate)
        {
            var data = LoadUnsafe();
            var result = update(data);
            SaveUnsafe(data);
            return result;
        }
    }

    private static string ProductKey(PurchaseRecord record)
    {
        var name = string.IsNullOrWhiteSpace(record.CanonicalName) ? record.Item : record.CanonicalName!;
        var product = KeyNormaliser.Product(name);
        var pack = KeyNormaliser.Pack(name) ?? KeyNormaliser.Pack(record.Item) ?? "unknown-size";
        return $"{product}|{pack}";
    }

    private void ImportHistoricalMappings(InventoryData data)
    {
        if (!Directory.Exists(_jobsDir)) return;
        var products = data.Products.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_jobsDir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!TryProperty(doc.RootElement, "priceChecks", out var checks) ||
                    !TryProperty(checks, "items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (!TryString(item, "name", out var name) || !TryString(item, "sourceUrl", out var url) ||
                        !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                        !uri.Host.EndsWith("trolley.co.uk", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = KeyNormaliser.PriceKey(name);
                    if (!products.TryGetValue(key, out var product) || product.SourceUrl is not null) continue;
                    var updated = product with { SourceUrl = uri.ToString(), MatchStatus = InventoryMatchStatus.Matched };
                    data.Products[data.Products.IndexOf(product)] = updated;
                    products[key] = updated;
                }
            }
            catch (JsonException) { /* A partial/old job file is not a deployment blocker. */ }
            catch (IOException) { /* Best-effort bootstrap only. */ }
        }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        return TryProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = property.GetString() ?? "");
    }

    private InventoryData LoadUnsafe()
    {
        if (!File.Exists(_path)) return new InventoryData();
        return JsonSerializer.Deserialize<InventoryData>(File.ReadAllText(_path), JsonOptions) ?? new InventoryData();
    }

    private void SaveUnsafe(InventoryData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tmp, _path, true);
    }

    private static InventoryData Clone(InventoryData data) =>
        JsonSerializer.Deserialize<InventoryData>(JsonSerializer.Serialize(data, JsonOptions), JsonOptions)!;
}
