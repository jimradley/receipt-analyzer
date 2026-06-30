using Microsoft.Extensions.Logging.Abstractions;
using ReceiptAnalyzer.Agent;
using ReceiptAnalyzer.Ledger;
using ReceiptAnalyzer.Ledger.Parsers;

namespace ReceiptAnalyzer.Tests;

public class PurchaseHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ph-" + Guid.NewGuid().ToString("N"));

    public PurchaseHistoryStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    private PurchaseHistoryStore NewStore() => new(_dir, NullLogger<PurchaseHistoryStore>.Instance);

    private static RawItem Item(string name, decimal qty, decimal unit) => new(name, qty, unit, qty * unit);

    [Fact]
    public void Save_and_load_round_trips()
    {
        var data = new PurchaseHistoryData
        {
            Records = { new PurchaseRecord("milk", "Milk", "Asda", new(2026, 6, 1), 1m, 1.45m, "job1") }
        };
        NewStore().Save(data);

        var record = Assert.Single(NewStore().Load().Records);
        Assert.Equal("Milk", record.Item);
        Assert.Equal("Asda", record.Retailer);
        Assert.Equal(new DateOnly(2026, 6, 1), record.Date);
        Assert.Equal(1.45m, record.UnitPrice);
    }

    [Fact]
    public void AppendReceipt_upserts_by_source_and_never_doubles()
    {
        var store = NewStore();
        var date = new DateOnly(2026, 6, 20);

        store.AppendReceipt("job1", "Asda", date, new[] { Item("Milk", 1, 1.45m), Item("Bread", 1, 0.95m) });
        Assert.Equal(2, store.Load().Records.Count);

        // Re-processing the same receipt (same source) replaces its rows, not adds.
        store.AppendReceipt("job1", "Asda", date, new[] { Item("Milk", 1, 1.45m), Item("Bread", 1, 0.95m), Item("Eggs", 1, 2.00m) });
        Assert.Equal(3, store.Load().Records.Count);

        // A different receipt adds.
        store.AppendReceipt("job2", "Lidl", date, new[] { Item("Milk", 1, 1.39m) });
        var records = store.Load().Records;
        Assert.Equal(4, records.Count);
        Assert.Equal(3, records.Count(r => r.Source == "job1")); // Milk, Bread, Eggs from the re-run
        Assert.Single(records, r => r.Source == "job2");
    }

    [Fact]
    public void AppendReceipt_normalises_the_key()
    {
        NewStore().AppendReceipt("j", "Asda", new(2026, 6, 1), new[] { Item("Semi Skimmed Milk", 1, 1.45m) });
        Assert.Equal("semi-skimmed-milk", Assert.Single(NewStore().Load().Records).Key);
    }

    [Fact]
    public void ReceiptReportParser_reads_retailer_date_and_items()
    {
        const string md = """
            # Shopping Report — 26 June 2026

            ## Receipt: Sainsbury's | 26 June 2026 | Paid: £10.00

            **Receipt math:** ✅ ok

            ### Items

            | Item | Qty | Unit | NOVA | 🇺🇸 | Notes |
            |---|---|---|---|---|---|
            | Milk 2.27L | 1 | £1.45 | 1 |  |  |
            | Mature Cheddar | 2 | £2.50 | 1 |  |  |

            ---

            ### Price Checks

            | Item | Paid | Cheapest | At | Saving |
            |---|---|---|---|---|
            | Branded Beans | £1.00 | £0.80 | Aldi | £0.20 |
            """;

        var parsed = ReceiptReportParser.Parse(md, null);
        Assert.NotNull(parsed);
        Assert.Equal("Sainsbury's", parsed!.Retailer);
        Assert.Equal(new DateOnly(2026, 6, 26), parsed.Date);
        Assert.Equal(2, parsed.Items.Count); // stops at the rule — the Price Checks table is not picked up
        Assert.Equal("Milk 2.27L", parsed.Items[0].Name);
        Assert.Equal(1.45m, parsed.Items[0].UnitPrice);
        Assert.Equal(2m, parsed.Items[1].Quantity);
        Assert.Equal(2.50m, parsed.Items[1].UnitPrice);
    }

    [Fact]
    public void ReceiptReportParser_falls_back_to_filename_date_when_heading_unparseable()
    {
        const string md = """
            ## Receipt: Asda

            ### Items

            | Item | Qty | Unit | NOVA | 🇺🇸 | Notes |
            |---|---|---|---|---|---|
            | Bananas | 1 | £0.80 | 1 |  |  |
            """;

        var parsed = ReceiptReportParser.Parse(md, new DateOnly(2026, 5, 4));
        Assert.NotNull(parsed);
        Assert.Equal(new DateOnly(2026, 5, 4), parsed!.Date);
        Assert.Equal("Asda", parsed.Retailer);
    }

    [Fact]
    public void Load_backfills_from_reports_and_skips_ledger_files()
    {
        File.WriteAllText(Path.Combine(_dir, "26-June-26.md"), Report("Asda", "26 June 2026", ("Milk", "£1.45"), ("Bread", "£0.95")));
        File.WriteAllText(Path.Combine(_dir, "19-June-26.md"), Report("Lidl", "19 June 2026", ("Milk", "£1.39")));
        File.WriteAllText(Path.Combine(_dir, "buy-elsewhere.md"), "| Item | Store Paid | Price Paid |\n|---|---|---|\n| X | Asda | £1.00 |");

        var records = NewStore().Load().Records;

        Assert.Equal(3, records.Count); // 2 + 1; buy-elsewhere skipped
        Assert.All(records, r => Assert.StartsWith("backfill:", r.Source));
        Assert.Equal(2, records.Count(r => r.Key == "milk"));
        Assert.Contains(records, r => r.Retailer == "Asda" && r.Key == "bread");
    }

    [Fact]
    public void Backfill_runs_once_then_load_reads_json()
    {
        File.WriteAllText(Path.Combine(_dir, "26-June-26.md"), Report("Asda", "26 June 2026", ("Milk", "£1.45")));

        var first = NewStore().Load();
        Assert.Single(first.Records);

        // Deleting the report must not change a subsequent load — it now reads the persisted json.
        File.Delete(Path.Combine(_dir, "26-June-26.md"));
        Assert.Single(NewStore().Load().Records);
    }

    private static string Report(string retailer, string date, params (string Name, string Price)[] items)
    {
        var rows = string.Join("\n", items.Select(i => $"| {i.Name} | 1 | {i.Price} | 1 |  |  |"));
        return $"""
            # Shopping Report — {date}

            ## Receipt: {retailer} | {date} | Paid: £9.99

            ### Items

            | Item | Qty | Unit | NOVA | 🇺🇸 | Notes |
            |---|---|---|---|---|---|
            {rows}

            ---
            """;
    }
}
