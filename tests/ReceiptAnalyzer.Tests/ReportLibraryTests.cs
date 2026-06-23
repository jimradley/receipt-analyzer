using ReceiptAnalyzer.Reports;

namespace ReceiptAnalyzer.Tests;

public class ReportLibraryTests : IDisposable
{
    private readonly string _dir;

    public ReportLibraryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ra-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string name, string content, DateTime? lastWrite = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        if (lastWrite is { } t) File.SetLastWriteTimeUtc(path, t);
    }

    [Fact]
    public void ListReports_excludes_ledgers_and_orders_newest_first()
    {
        Write("20-June-26.md", "newer", new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc));
        Write("13-April-26.md", "older", new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc));
        Write(ReportLibrary.BuyElsewhereFile, "ledger");
        Write(ReportLibrary.AlternativesFile, "ledger");

        var reports = new ReportLibrary(_dir).ListReports();

        Assert.Equal(2, reports.Count);
        Assert.Equal("20-June-26.md", reports[0].Name);   // newest first
        Assert.Equal("13-April-26.md", reports[1].Name);
        Assert.DoesNotContain(reports, r => r.Name == ReportLibrary.BuyElsewhereFile);
    }

    [Fact]
    public void ListReports_returns_empty_when_directory_absent()
    {
        var missing = Path.Combine(_dir, "does-not-exist");
        Assert.Empty(new ReportLibrary(missing).ListReports());
    }

    [Fact]
    public void ReadReport_returns_content_for_known_file()
    {
        Write("20-June-26.md", "# report body");
        Assert.Equal("# report body", new ReportLibrary(_dir).ReadReport("20-June-26.md"));
    }

    [Fact]
    public void ReadReport_returns_null_for_ledger_or_missing_or_unsafe_names()
    {
        Write(ReportLibrary.BuyElsewhereFile, "ledger");
        var lib = new ReportLibrary(_dir);

        Assert.Null(lib.ReadReport(ReportLibrary.BuyElsewhereFile));   // ledgers not served as reports
        Assert.Null(lib.ReadReport("nope.md"));                       // missing
        Assert.Null(lib.ReadReport("../secrets.md"));                 // traversal
        Assert.Null(lib.ReadReport("20-June-26.txt"));                // wrong extension
    }

    [Fact]
    public void ReadLedger_accepts_slug_or_filename_and_rejects_others()
    {
        Write(ReportLibrary.BuyElsewhereFile, "buy body");
        Write(ReportLibrary.AlternativesFile, "alt body");
        var lib = new ReportLibrary(_dir);

        Assert.Equal("buy body", lib.ReadLedger("buy-elsewhere"));
        Assert.Equal("alt body", lib.ReadLedger("alternatives.md"));
        Assert.Null(lib.ReadLedger("ledger"));   // unknown name
    }
}
