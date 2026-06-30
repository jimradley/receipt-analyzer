using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class ProductIdentityTests
{
    [Theory]
    [InlineData("MISSION WRAPS", "1 MISSION WRAPS", "mission-wraps")]
    [InlineData("M MISSION WRAPS", "1 M MISSION WRAPS", "mission-wraps")]
    public void Receipt_prefixes_do_not_create_duplicate_products(string left, string right, string expected)
    {
        Assert.Equal(expected, KeyNormaliser.Product(left));
        Assert.Equal(expected, KeyNormaliser.Product(right));
    }

    [Fact]
    public void Price_identity_separates_pack_sizes_but_product_identity_does_not()
    {
        Assert.Equal(KeyNormaliser.Product("Maltesers 100g"), KeyNormaliser.Product("Maltesers 300g"));
        Assert.NotEqual(KeyNormaliser.PriceKey("Maltesers 100g"), KeyNormaliser.PriceKey("Maltesers 300g"));
    }

    [Fact]
    public void Uk_catalog_is_authoritative_for_in_season_status()
    {
        var item = new ProduceItem(4, "British Asparagus");
        var contradictoryModel = new SeasonalityAssessment(4, "Other", false, "Peru", "Never");

        var result = UkSeasonalityCatalog.Apply(item, 5, contradictoryModel);

        Assert.True(result.IsInSeason);
        Assert.Null(result.LikelyOrigin);
        Assert.Contains("May", result.UkSeasonMonths);
    }

    [Theory]
    [InlineData("Basmati Rice")]
    [InlineData("Dried Mixed Herbs")]
    [InlineData("Red Wine")]
    public void Non_produce_is_not_sent_for_seasonality(string name)
        => Assert.False(UkSeasonalityCatalog.TryResolve(name, out _));
}
