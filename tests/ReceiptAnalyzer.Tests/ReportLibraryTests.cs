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
        Write("20-June-26-Morrisons-a1b2c3d4.md", "newer", new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc));
        Write("13-April-26-Asda-e5f6a7b8.md", "older", new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc));
        Write(ReportLibrary.BuyElsewhereFile, "ledger");
        Write(ReportLibrary.AlternativesFile, "ledger");

        var reports = new ReportLibrary(_dir).ListReports();

        Assert.Equal(2, reports.Count);
        Assert.Equal("20-June-26-Morrisons-a1b2c3d4.md", reports[0].Name);   // newest first
        Assert.Equal("13-April-26-Asda-e5f6a7b8.md", reports[1].Name);
        Assert.DoesNotContain(reports, r => r.Name == ReportLibrary.BuyElsewhereFile);
    }

    [Fact]
    public void ListReports_ignores_files_not_written_by_the_pipeline()
    {
        // A stray file dropped into the shared output folder by something other than this app's own
        // pipeline (e.g. an old manual-format report with no retailer/job-id suffix) must never be
        // surfaced in History as if it were a genuine analysis.
        Write("22-August-26.md", "legacy manual report", new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc));
        Write("22-August-26-Morrisons-f6290826.md", "real report", new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc));

        var reports = new ReportLibrary(_dir).ListReports();

        var report = Assert.Single(reports);
        Assert.Equal("22-August-26-Morrisons-f6290826.md", report.Name);
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
        Write("20-June-26-Morrisons-a1b2c3d4.md", "# report body");
        Assert.Equal("# report body", new ReportLibrary(_dir).ReadReport("20-June-26-Morrisons-a1b2c3d4.md"));
    }

    [Fact]
    public void ReadReport_returns_null_for_ledger_or_missing_or_unsafe_or_non_pipeline_names()
    {
        Write(ReportLibrary.BuyElsewhereFile, "ledger");
        Write("20-June-26.md", "legacy manual report"); // not written by the pipeline
        var lib = new ReportLibrary(_dir);

        Assert.Null(lib.ReadReport(ReportLibrary.BuyElsewhereFile));            // ledgers not served as reports
        Assert.Null(lib.ReadReport("nope-Morrisons-a1b2c3d4.md"));              // missing
        Assert.Null(lib.ReadReport("../secrets-Morrisons-a1b2c3d4.md"));        // traversal
        Assert.Null(lib.ReadReport("20-June-26-Morrisons-a1b2c3d4.txt"));       // wrong extension
        Assert.Null(lib.ReadReport("20-June-26.md"));                          // not a pipeline filename
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
