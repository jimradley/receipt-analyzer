using ReceiptAnalyzer.Agent;

namespace ReceiptAnalyzer.Tests;

public class AlcoholNovaGuardTests
{
    private static RawItem Item(string name) => new(name, 1m, 1m, 1m);

    private static ItemClassification Cls(int index, int? nova, bool american = false, string? swap = null)
        => new(index, nova, american, null, null, false, swap, null);

    [Theory]
    [InlineData("YELLOW TAIL SHIRAZ")]
    [InlineData("Wandered Chardonnay")]
    [InlineData("THE BEST PROSECCO")]
    [InlineData("Brancott Estate Sauvignon Blanc 75cl")]
    [InlineData("Gordon's Gin 70cl")]
    [InlineData("Aspall Cider")]
    public void Forces_recognised_alcohol_to_nova_one(string name)
    {
        var items = new[] { Item(name) };
        var input = new ItemClassifications(new[] { Cls(0, 4, swap: "try a cleaner option") });

        var result = AlcoholNovaGuard.Apply(items, input);
        Assert.Equal(1, result.Items[0].NovaLevel);
        Assert.Null(result.Items[0].SwapSuggestion); // swap dropped (was only there for the wrong NOVA)
    }

    [Theory]
    [InlineData("Ginger Beer")]      // soft drink, not alcohol
    [InlineData("Port Salut Cheese")] // "port" must not match
    [InlineData("Real Ale Chutney")]  // "ale" must not match
    [InlineData("McVitie's Hob Nobs")]
    [InlineData("Wine Gums")]          // sweets — acceptable false-negative on guard? must NOT be forced
    public void Leaves_non_alcohol_untouched(string name)
    {
        var items = new[] { Item(name) };
        var input = new ItemClassifications(new[] { Cls(0, 4, swap: "swap me") });

        var result = AlcoholNovaGuard.Apply(items, input);
        Assert.Equal(4, result.Items[0].NovaLevel);
        Assert.Equal("swap me", result.Items[0].SwapSuggestion);
    }

    [Fact]
    public void Keeps_swap_when_alcohol_is_also_american()
    {
        var items = new[] { Item("Jack Daniels Whiskey 70cl") };
        var input = new ItemClassifications(new[] { Cls(0, 4, american: true, swap: "British distillery alternative") });

        var result = AlcoholNovaGuard.Apply(items, input);
        Assert.Equal(1, result.Items[0].NovaLevel);
        Assert.Equal("British distillery alternative", result.Items[0].SwapSuggestion);
    }

    [Fact]
    public void Already_nova_one_or_null_passes_through()
    {
        var items = new[] { Item("Yellow Tail Shiraz"), Item("Carex Hand Wash") };
        var input = new ItemClassifications(new[] { Cls(0, 1), Cls(1, null) });

        var result = AlcoholNovaGuard.Apply(items, input);
        Assert.Equal(1, result.Items[0].NovaLevel);
        Assert.Null(result.Items[1].NovaLevel);
    }
}
