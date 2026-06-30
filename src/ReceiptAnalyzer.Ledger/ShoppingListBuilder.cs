namespace ReceiptAnalyzer.Ledger;

public sealed record StoreGroceryItem(
    string Item, decimal BestPrice, decimal Saving, decimal PricePaid, string StorePaid, string LastSeen);

public sealed record StoreWine(string Grape, string Wine, string Price, string Tier);

public sealed record StoreShoppingList(
    string Store, decimal TotalSaving,
    IReadOnlyList<StoreGroceryItem> Groceries, IReadOnlyList<StoreWine> Wines);

/// <summary>The per-store shopping list plus a count of buy-elsewhere items hidden because their only
/// cheaper option is a store outside the allowed set (e.g. Tesco / Co-op).</summary>
public sealed record ShoppingListResult(IReadOnlyList<StoreShoppingList> Stores, int HiddenGroceryItems);

/// <summary>
/// Pivots the buy-elsewhere ledger by recommended store and merges in the per-store wine
/// recommendations, producing one actionable "what to buy here" list per allowed store. Stores are
/// ordered by total grocery saving (most worth a trip first); wine-only stores fall to the bottom.
/// </summary>
public static class ShoppingListBuilder
{
    public static ShoppingListResult Build(LedgerData ledger, IReadOnlyList<WineRecommendation> wines)
    {
        // store → (normalised item key → best grocery item)
        var groceriesByStore = new Dictionary<string, Dictionary<string, StoreGroceryItem>>(StringComparer.OrdinalIgnoreCase);
        var hidden = 0;

        foreach (var entry in ledger.BuyElsewhere)
        {
            var entryStores = StoreCatalog.ExtractAllowed(entry.Where);
            if (entryStores.Count == 0) { hidden++; continue; }

            foreach (var store in entryStores)
            {
                var items = groceriesByStore.TryGetValue(store, out var existing)
                    ? existing
                    : groceriesByStore[store] = new Dictionary<string, StoreGroceryItem>();

                var item = new StoreGroceryItem(
                    entry.Item, entry.BestPrice, entry.Saving, entry.PricePaid, entry.StorePaid, entry.LastSeen);

                // De-dup an item appearing for the same store across entries; keep the biggest saving.
                if (!items.TryGetValue(entry.Key, out var prev) || item.Saving > prev.Saving)
                    items[entry.Key] = item;
            }
        }

        var winesByStore = wines
            .GroupBy(w => w.Store, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var allStores = groceriesByStore.Keys
            .Concat(winesByStore.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var stores = new List<StoreShoppingList>();
        foreach (var store in allStores)
        {
            var groceries = (groceriesByStore.GetValueOrDefault(store)?.Values ?? Enumerable.Empty<StoreGroceryItem>())
                .OrderByDescending(g => g.Saving)
                .ThenBy(g => g.Item, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var storeWines = (winesByStore.GetValueOrDefault(store) ?? [])
                .OrderBy(w => WineCatalog.TierRank(w.Tier))
                .ThenBy(w => w.Grape, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Wine, StringComparer.OrdinalIgnoreCase)
                .Select(w => new StoreWine(w.Grape, w.Wine, w.Price, w.Tier))
                .ToList();

            stores.Add(new StoreShoppingList(store, groceries.Sum(g => g.Saving), groceries, storeWines));
        }

        var ordered = stores
            .OrderByDescending(s => s.TotalSaving)
            .ThenBy(s => s.Store, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ShoppingListResult(ordered, hidden);
    }
}
