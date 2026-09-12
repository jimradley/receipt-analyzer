using Microsoft.JSInterop;
using ReceiptAnalyzer.Pwa;

namespace ReceiptAnalyzer.Tests;

public class ShoppingListComposerTests
{
    [Fact]
    public void Builds_separate_store_sections_and_merges_duplicate_items_within_store()
    {
        var selections = new[]
        {
            Selection("wine:asda", "rioja", "House Rioja", "Alcohol", 12, "Asda"),
            Selection("milk:staple", "semi-skimmed-milk", "Semi Skimmed Milk", "Milk, dairy & eggs", 2, null),
            Selection("milk:asda", "semi-skimmed-milk", "Semi Skimmed Milk 2L", "Milk, dairy & eggs", 2, "Asda"),
            Selection("milk:morrisons", "semi-skimmed-milk", "Semi Skimmed Milk 2L", "Milk, dairy & eggs", 2, "Morrisons"),
            Selection("banana", "bananas", "Bananas", "Fresh produce", 0, null),
            Selection("apple", "apples", "Apples", "Fresh produce", 0, "Lidl"),
            Selection("beans", "baked-beans", "Baked Beans", "Tins & jars", 6, "Aldi")
        };

        var result = ShoppingListComposer.Build(selections);

        Assert.Equal(string.Join('\n',
            "## Aldi",
            "Tins & jars",
            "- Baked Beans",
            "",
            "## Asda",
            "Milk, dairy & eggs",
            "- Semi Skimmed Milk 2L",
            "",
            "Alcohol",
            "- House Rioja",
            "",
            "## Lidl",
            "Fresh produce",
            "- Apples",
            "",
            "## Morrisons",
            "Milk, dairy & eggs",
            "- Semi Skimmed Milk 2L",
            "",
            "## Store not specified",
            "Fresh produce",
            "- Bananas",
            "",
            "Milk, dairy & eggs",
            "- Semi Skimmed Milk"), result);
    }

    [Fact]
    public void Empty_selection_produces_no_list() =>
        Assert.Empty(ShoppingListComposer.Build([]));

    [Fact]
    public async Task Basket_persists_across_instances_and_removes_only_the_unchecked_source()
    {
        var js = new LocalStorageJsRuntime();
        var first = new ShoppingBasketService(js);
        var staple = Selection("staple:milk", "milk", "Milk", "Milk, dairy & eggs", 2, null);
        var store = Selection("store:milk", "milk", "Milk 2L", "Milk, dairy & eggs", 2, "Asda");

        await first.SetAsync(staple, true);
        await first.SetAsync(store, true);
        Assert.Equal(1, first.ItemCount);

        var restored = new ShoppingBasketService(js);
        await restored.InitializeAsync();
        Assert.True(restored.Contains(staple.Id));
        Assert.True(restored.Contains(store.Id));

        await restored.SetAsync(staple, false);
        Assert.False(restored.Contains(staple.Id));
        Assert.True(restored.Contains(store.Id));
        Assert.Equal(1, restored.ItemCount);
    }

    private static ShoppingSelection Selection(
        string id, string key, string item, string aisle, int order, string? store) =>
        new(id, key, item, aisle, order, store);

    private sealed class LocalStorageJsRuntime : IJSRuntime
    {
        private string? _stored;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem")
                return ValueTask.FromResult((TValue)(object)(_stored ?? string.Empty));
            if (identifier == "localStorage.setItem")
                _stored = args?[1]?.ToString();
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
