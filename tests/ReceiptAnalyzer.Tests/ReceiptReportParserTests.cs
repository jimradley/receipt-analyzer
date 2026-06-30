using ReceiptAnalyzer.Ledger.Parsers;

namespace ReceiptAnalyzer.Tests;

public class ReceiptReportParserTests
{
    // Mirrors ReportRenderer's Items-table format: | Item | Qty | Unit | NOVA | 🇺🇸 | Notes |
    private const string Report = """
        # Shopping Report — 12 June 2026

        ## Receipt: Morrisons | 12 June 2026 | Paid: £8.50

        ### Items

        | Item | Qty | Unit | NOVA | 🇺🇸 | Notes |
        |---|---|---|---|---|---|
        | Peppers | 1 | £1.20 | 1 |  |  |
        | Coca-Cola | 2 | £1.50 | **4** 🚩 | **🇺🇸 Coca-Cola Co** | See swap below |
        | Hovis Bread | 1 | £1.10 | 3 |  | See swap below |

        ---
        """;

    [Fact]
    public void Parses_retailer_date_and_items()
    {
        var parsed = ReceiptReportParser.Parse(Report, null);

        Assert.NotNull(parsed);
        Assert.Equal("Morrisons", parsed!.Retailer);
        Assert.Equal(new DateOnly(2026, 6, 12), parsed.Date);
        Assert.Equal(3, parsed.Items.Count);
    }

    [Fact]
    public void Extracts_nova_level_and_us_ownership()
    {
        var items = ReceiptReportParser.Parse(Report, null)!.Items;

        var peppers = items[0];
        Assert.Equal(1, peppers.NovaLevel);
        Assert.Null(peppers.IsAmerican);

        var coke = items[1];
        Assert.Equal(4, coke.NovaLevel);
        Assert.True(coke.IsAmerican);
        Assert.Equal("Coca-Cola Co", coke.ParentCompany);

        var bread = items[2];
        Assert.Equal(3, bread.NovaLevel);
        Assert.Null(bread.IsAmerican); // empty flag cell → unknown (not flagged American)
    }
}
