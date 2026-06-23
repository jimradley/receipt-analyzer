using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class KeyNormaliserTests
{
    [Theory]
    [InlineData("Cathedral City Mature Cheddar 350g", "cathedral-city-mature-cheddar-350g")]
    [InlineData("  Heinz  Beanz  ", "heinz-beanz")]
    [InlineData("Sainsbury's Milk", "sainsbury-s-milk")]
    [InlineData("Coca-Cola (2L)", "coca-cola-2l")]
    [InlineData("M&S Hummus", "m-s-hummus")]
    public void Normalise_lowercases_strips_punctuation_and_hyphenates(string input, string expected)
    {
        Assert.Equal(expected, KeyNormaliser.Normalise(input));
    }

    [Fact]
    public void Normalise_is_stable_for_same_item_with_different_casing_and_spacing()
    {
        Assert.Equal(
            KeyNormaliser.Normalise("Lurpak Spreadable"),
            KeyNormaliser.Normalise("  LURPAK   spreadable "));
    }
}
