using System.Text.RegularExpressions;

namespace ReceiptAnalyzer.Reports;

public sealed record ReportSummary(string Name, DateTimeOffset Modified);

/// <summary>
/// Read-only access to the markdown artefacts written into the output directory:
/// the per-receipt reports plus the two rolling ledgers. Filename-only access — no
/// path traversal outside the configured directory.
/// </summary>
public sealed class ReportLibrary
{
    public const string BuyElsewhereFile = "buy-elsewhere.md";
    public const string AlternativesFile = "alternatives.md";

    private static readonly HashSet<string> LedgerFiles =
        new(StringComparer.OrdinalIgnoreCase) { BuyElsewhereFile, AlternativesFile };

    // Matches only the filenames AnalysisPipeline itself writes: "{dd-MMMM-yy}-{retailer}-{8 hex}.md"
    // (e.g. "22-August-26-Morrisons-f6290826.md"). Anything else in the output folder — a stray file
    // dropped there by something outside this app — is never treated as a real analysis, so it can't
    // masquerade as a duplicate app run in History or get back-filled into purchase history.
    private static readonly Regex PipelineReportFileName =
        new(@"^\d{2}-[A-Za-z]+-\d{2}-.+-[0-9a-f]{8}\.md$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsPipelineReportFileName(string fileName) => PipelineReportFileName.IsMatch(fileName);

    private readonly string _dir;

    public ReportLibrary(string outputDir) => _dir = outputDir;

    /// <summary>Per-receipt reports (date-retailer-job markdown files), newest first. Ledgers are excluded.</summary>
    public IReadOnlyList<ReportSummary> ListReports()
    {
        if (!Directory.Exists(_dir)) return [];

        return new DirectoryInfo(_dir)
            .EnumerateFiles("*.md", SearchOption.TopDirectoryOnly)
            .Where(f => !LedgerFiles.Contains(f.Name) && IsPipelineReportFileName(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new ReportSummary(f.Name, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    /// <summary>Markdown of a single report by filename, or null if it is missing, unsafe, or not a
    /// report the pipeline itself wrote.</summary>
    public string? ReadReport(string name) =>
        IsPipelineReportFileName(name) ? ReadSafe(name, allowLedgers: false) : null;

    /// <summary>Markdown of one of the two ledgers ("buy-elsewhere" / "alternatives" or their filenames).</summary>
    public string? ReadLedger(string which)
    {
        var file = which.ToLowerInvariant() switch
        {
            "buy-elsewhere" or "buy-elsewhere.md" => BuyElsewhereFile,
            "alternatives" or "alternatives.md" => AlternativesFile,
            _ => null
        };
        return file is null ? null : ReadSafe(file, allowLedgers: true);
    }

    private string? ReadSafe(string name, bool allowLedgers)
    {
        // Reject anything that isn't a bare filename (defeats ../ traversal and absolute paths).
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name) return null;
        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        if (!allowLedgers && LedgerFiles.Contains(name)) return null;

        var path = Path.Combine(_dir, name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
