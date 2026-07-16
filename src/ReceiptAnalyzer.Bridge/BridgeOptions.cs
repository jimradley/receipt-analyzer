namespace ReceiptAnalyzer.Bridge;

/// <summary>Runtime knobs for the bridge — where the `claude` CLI lives, its default limits, and the
/// container-path → host-path translation for receipt images on the shared bind mount.</summary>
public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    /// <summary>
    /// Container path prefix → host path prefix. The API container writes a receipt image to a
    /// bind-mounted folder using its own (container) path; this bridge — running on the host — needs
    /// the equivalent host path to hand to the `claude` CLI's Read tool.
    /// </summary>
    public Dictionary<string, string> PathMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/data/shopping"] = @"C:\AI\Projects\Shopping",
    };

    /// <summary>Executable name/path for the Claude Code CLI. Must be resolvable on PATH, or a full path.</summary>
    public string ClaudeExecutable { get; set; } = "claude";

    public int DefaultTimeoutSeconds { get; set; } = 600;

    /// <summary>Cap on agentic turns per call — a non-interactive safety net, not a normal ceiling.</summary>
    public int MaxTurns { get; set; } = 25;

    public string DefaultModel { get; set; } = "claude-sonnet-5";

    /// <summary>Comma-separated tool allowlist used when the caller doesn't specify one.</summary>
    public string DefaultAllowedTools { get; set; } = "Read,WebSearch";
}
