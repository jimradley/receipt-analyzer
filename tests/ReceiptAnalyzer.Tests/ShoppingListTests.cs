using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class StoreCatalogTests
{
    [Theory]
    [InlineData("Asda", "Asda")]
    [InlineData("Sainsbury's", "Sainsbury's")]
    [InlineData("sainsburys", "Sainsbury's")]
    [InlineData("Waitrose & Partners", "Waitrose")]
    [InlineData("Asda (offer)", "Asda")]
    [InlineData("Tesco (Clubcard)", null)]
    [InlineData("Tesco — any 3 for £9", null)]
    [InlineData("M&S", null)]
    [InlineData("Majestic", null)]
    [InlineData("Co-op", null)]
    public void Canonical_maps_allowed_and_drops_forbidden(string raw, string? expected)
        => Assert.Equal(expected, StoreCatalog.Canonical(raw));

    [Fact]
    public void ExtractAllowed_splits_multi_store_and_drops_forbidden()
    {
        Assert.Equal(new[] { "Asda", "B&M" }, StoreCatalog.ExtractAllowed("Asda / B&M"));
        Assert.Equal(new[] { "Asda" }, StoreCatalog.ExtractAllowed("Tesco / Asda"));   // Tesco dropped
        Assert.Empty(StoreCatalog.ExtractAllowed("Tesco / Co-op"));
        Assert.Equal(new[] { "Morrisons", "Ocado" }, StoreCatalog.ExtractAllowed("Morrisons / Ocado"));
    }
}

public class WineCatalogTests
{
    private static string WriteWineDir(params (string file, string content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (file, content) in files) File.WriteAllText(Path.Combine(dir, file), content);
        return dir;
    }

    [Fact]
    public void Load_keeps_recommended_tiers_drops_arse_unrated_and_forbidden_stores()
    {
        var dir = WriteWineDir(("sauvignon-blanc.md", """
            # Sauvignon Blanc Recommendations

            ## CLASS

            | Wine | Retailer | Price |
            |------|----------|-------|
            | Villa Maria | Sainsbury's | £11.00 (£8.50 w. Nectar) |
            | Wairau Cove | Tesco | £10.00 |

            ## PASS

            | Wine | Retailer | Price |
            |------|----------|-------|
            | Nobilo | Sainsbury's | £9.25 |

            ## ARSE

            | Wine | Retailer | Price |
            |------|----------|-------|
            | Plonk | Asda | £4.00 |

            ## UNRATED

            | Wine | Retailer | Price (approx) |
            |------|----------|----------------|
            | Kim Crawford | Tesco / Waitrose / M&S | ~£10–12 |
            """));

        var recs = new WineCatalog(dir).Load();
        Directory.Delete(dir, true);

        Assert.Equal(2, recs.Count); // Villa Maria (CLASS), Nobilo (PASS); Tesco/ARSE/UNRATED dropped
        var villa = Assert.Single(recs, r => r.Wine == "Villa Maria");
        Assert.Equal("Sainsbury's", villa.Store);
        Assert.Equal("Sauvignon Blanc", villa.Grape);
        Assert.Equal("Top pick", villa.Tier);
        Assert.Equal("£11.00 (£8.50 w. Nectar)", villa.Price);
        Assert.Equal("Also good", Assert.Single(recs, r => r.Wine == "Nobilo").Tier);
    }

    [Fact]
    public void Load_handles_tom_pick_the_rest_headers_and_extra_columns()
    {
        var dir = WriteWineDir(("chardonnay.md", """
            # Chardonnay Recommendations

            ## RECOMMENDED

            | Wine | Retailer | Price | Source |
            |------|----------|-------|--------|
            | Big & Buttery | Morrisons | ~£8 | WineFolk |
            | Cask & Cream | Tesco | £11.00 | |
            """));

        var recs = new WineCatalog(dir).Load();
        Directory.Delete(dir, true);

        var rec = Assert.Single(recs); // Tesco row dropped
        Assert.Equal("Morrisons", rec.Store);
        Assert.Equal("Big & Buttery", rec.Wine);
        Assert.Equal("~£8", rec.Price);
        Assert.Equal("Top pick", rec.Tier);
    }

    [Fact]
    public void Load_returns_empty_when_dir_missing()
        => Assert.Empty(new WineCatalog(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid())).Load());
}

public class ShoppingListBuilderTests
{
    private static BuyElsewhereEntry Entry(string item, string where, decimal saving)
        => new(KeyNormaliser.Normalise(item), item, "Morrisons", 3.00m, 3.00m - saving, where, saving, "2026-06-01");

    [Fact]
    public void Build_pivots_by_store_counts_hidden_and_orders_by_saving()
    {
        var ledger = new LedgerData
        {
            BuyElsewhere =
            {
                Entry("Beans", "Asda / B&M", 0.71m),
                Entry("Cheddar", "Tesco (Clubcard)", 1.10m), // only Tesco → hidden
                Entry("Almond Milk", "Morrisons", 0.80m),
            }
        };
        var wines = new List<WineRecommendation>
        {
            new("Asda", "Chablis", "Asda Exceptional Chablis", "£13.00", "Also good"),
            new("Waitrose", "Chablis", "Esprit de Chablis", "£14.75", "Top pick"),
        };

        var result = ShoppingListBuilder.Build(ledger, wines);

        Assert.Equal(1, result.HiddenGroceryItems); // Cheddar (Tesco only)

        // Asda: Beans (0.71) appears; B&M also gets Beans. Morrisons: Almond Milk (0.80). Waitrose: wine only.
        var asda = Assert.Single(result.Stores, s => s.Store == "Asda");
        Assert.Equal("Beans", Assert.Single(asda.Groceries).Item);
        Assert.Single(asda.Wines);

        var bm = Assert.Single(result.Stores, s => s.Store == "B&M");
        Assert.Equal("Beans", Assert.Single(bm.Groceries).Item);

        var waitrose = Assert.Single(result.Stores, s => s.Store == "Waitrose");
        Assert.Empty(waitrose.Groceries);
        Assert.Equal("Esprit de Chablis", Assert.Single(waitrose.Wines).Wine);

        // Ordered by total saving desc: Morrisons (0.80) before Asda (0.71); wine-only Waitrose last.
        Assert.Equal("Morrisons", result.Stores[0].Store);
        Assert.Equal("Asda", result.Stores[1].Store);
        Assert.Equal("Waitrose", result.Stores[^1].Store);
    }

    [Fact]
    public void Build_dedupes_same_item_per_store_keeping_biggest_saving()
    {
        var ledger = new LedgerData
        {
            BuyElsewhere = { Entry("Skyr", "Sainsbury's", 0.25m), Entry("Skyr", "Sainsbury's", 2.50m) }
        };

        var sains = Assert.Single(ShoppingListBuilder.Build(ledger, []).Stores);
        var item = Assert.Single(sains.Groceries);
        Assert.Equal(2.50m, item.Saving);
    }
}
