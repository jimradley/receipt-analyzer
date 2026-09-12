using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;

namespace ReceiptAnalyzer.Pwa;

public sealed record ShoppingSelection(
    string Id,
    string ProductKey,
    string Item,
    string Aisle,
    int AisleOrder,
    string? Store);

/// <summary>Shared browser-local selection state for the Stores and Staples pages.</summary>
public sealed class ShoppingBasketService(IJSRuntime js)
{
    private const string StorageKey = "ra_shopping_basket_v1";
    private readonly Dictionary<string, ShoppingSelection> _selections = new(StringComparer.Ordinal);
    private bool _loaded;

    public event Action? Changed;

    public IReadOnlyCollection<ShoppingSelection> Selections => _selections.Values;
    public int ItemCount => _selections.Values.Select(x => x.ProductKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            var saved = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<List<ShoppingSelection>>(json);
            if (saved is not null)
            {
                foreach (var selection in saved.Where(IsValid))
                    _selections[selection.Id] = selection;
            }
        }
        catch
        {
            // Storage can be unavailable in privacy modes. Keep an in-memory basket for this run.
        }

        Changed?.Invoke();
    }

    public bool Contains(string id) => _selections.ContainsKey(id);

    public async Task SetAsync(ShoppingSelection selection, bool selected)
    {
        await InitializeAsync();
        Apply(selection, selected);
        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task SetManyAsync(IEnumerable<ShoppingSelection> selections, bool selected)
    {
        await InitializeAsync();
        foreach (var selection in selections)
            Apply(selection, selected);
        await PersistAsync();
        Changed?.Invoke();
    }

    private void Apply(ShoppingSelection selection, bool selected)
    {
        if (selected)
            _selections[selection.Id] = selection;
        else
            _selections.Remove(selection.Id);
    }

    private async Task PersistAsync()
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey,
                JsonSerializer.Serialize(_selections.Values));
        }
        catch
        {
            // The current page still retains the selection even if persistence is unavailable.
        }
    }

    private static bool IsValid(ShoppingSelection selection) =>
        !string.IsNullOrWhiteSpace(selection.Id) &&
        !string.IsNullOrWhiteSpace(selection.ProductKey) &&
        !string.IsNullOrWhiteSpace(selection.Item) &&
        !string.IsNullOrWhiteSpace(selection.Aisle);
}

public static class ShoppingListComposer
{
    public static string Build(IEnumerable<ShoppingSelection> selections)
    {
        var output = new StringBuilder();
        var storeGroups = selections
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Store) ? "Store not specified" : x.Store.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var store in storeGroups)
        {
            if (output.Length > 0) output.Append('\n');
            output.Append("## ").Append(store.Key).Append('\n');

            var items = store
                .GroupBy(x => x.ProductKey, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var preferred = group.OrderByDescending(x => x.Item.Length).First();
                    var aisle = group.OrderBy(x => x.AisleOrder).First();
                    return new ComposedItem(preferred.Item.Trim(), aisle.Aisle, aisle.AisleOrder);
                })
                .OrderBy(x => x.AisleOrder)
                .ThenBy(x => x.Aisle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Item, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var firstAisle = true;
            foreach (var aisle in items.GroupBy(x => new { x.AisleOrder, x.Aisle }))
            {
                if (!firstAisle) output.Append('\n');
                firstAisle = false;
                output.Append(aisle.Key.Aisle).Append('\n');
                foreach (var item in aisle)
                    output.Append("- ").Append(item.Item).Append('\n');
            }
        }

        return output.ToString().TrimEnd();
    }

    private sealed record ComposedItem(string Item, string Aisle, int AisleOrder);
}
