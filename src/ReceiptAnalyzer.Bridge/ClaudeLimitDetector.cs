namespace ReceiptAnalyzer.Bridge;

/// <summary>Recognises terminal Claude Code allowance errors that are safe to fail over to Codex.</summary>
public static class ClaudeLimitDetector
{
    private static readonly string[] Markers =
    [
        "hit your session limit",
        "hit your weekly limit",
        "hit your monthly limit",
        "hit your monthly spend limit",
        "usage limit reached",
    ];

    public static bool IsLimitError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return Markers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
