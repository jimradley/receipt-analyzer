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

    private readonly string _dir;

    public ReportLibrary(string outputDir) => _dir = outputDir;

    /// <summary>Per-receipt reports (date-retailer-job markdown files), newest first. Ledgers are excluded.</summary>
    public IReadOnlyList<ReportSummary> ListReports()
    {
        if (!Directory.Exists(_dir)) return [];

        return new DirectoryInfo(_dir)
            .EnumerateFiles("*.md", SearchOption.TopDirectoryOnly)
            .Where(f => !LedgerFiles.Contains(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new ReportSummary(f.Name, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToList();
    }

    /// <summary>Markdown of a single report by filename, or null if it is missing or the name is unsafe.</summary>
    public string? ReadReport(string name) => ReadSafe(name, allowLedgers: false);

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
