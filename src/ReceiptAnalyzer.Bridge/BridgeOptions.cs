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

    /// <summary>Comma-separated tools requested when the caller doesn't specify any (still filtered
    /// through <see cref="ToolAllowlist"/>).</summary>
    public string DefaultAllowedTools { get; set; } = "Read,WebSearch";

    /// <summary>
    /// The ONLY tools the bridge will ever pass to the host `claude` CLI. A caller-supplied tool list
    /// is intersected against this — the `claude` process runs un-sandboxed on the host, so allowing
    /// a request to add Bash/Write/Edit would be remote code execution. Read is needed for receipt
    /// images; WebSearch for price checks. Do not add execution/file-write tools here.
    /// </summary>
    public HashSet<string> ToolAllowlist { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read", "WebSearch",
    };
}
