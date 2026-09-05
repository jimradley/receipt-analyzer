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

    /// <summary>
    /// When Claude Code reports that its subscription/session allowance is exhausted, retry the
    /// same request through the host user's signed-in Codex CLI. This uses the ChatGPT account,
    /// rather than the metered OpenAI API fallback configured in ReceiptAnalyzer.Api.
    /// </summary>
    public bool CodexFallbackEnabled { get; set; } = true;

    /// <summary>Native executable for the signed-in Codex CLI (not the npm <c>.cmd</c> shim).</summary>
    public string CodexExecutable { get; set; } = "codex.exe";

    /// <summary>Model used only after Claude Code reports an allowance/session limit.</summary>
    public string CodexFallbackModel { get; set; } = "gpt-5.6-terra";

    /// <summary>Low-cost model permitted for explicit, tool-free structured matching calls.</summary>
    public string CodexMatcherModel { get; set; } = "gpt-5.6-luna";

    /// <summary>Reasoning effort used by the Codex fallback.</summary>
    public string CodexReasoningEffort { get; set; } = "medium";

    public int DefaultTimeoutSeconds { get; set; } = 600;

    /// <summary>Cap on agentic turns per call — a non-interactive safety net, not a normal ceiling.</summary>
    public int MaxTurns { get; set; } = 25;

    public string DefaultModel { get; set; } = "claude-sonnet-5";

    /// <summary>Comma-separated tools requested when the caller doesn't specify any (still filtered
    /// through <see cref="ToolAllowlist"/>).</summary>
    public string DefaultAllowedTools { get; set; } = "Read,WebSearch";

    /// <summary>
    /// Tools explicitly denied on every call. An explicit deny beats any allow rule, including ones
    /// the host user's own settings file would contribute. Belt-and-braces alongside
    /// <see cref="ToolAllowlist"/>: the allowlist controls what we *ask* for, this controls what the
    /// CLI will honour if something else tries to grant it.
    /// </summary>
    public string DisallowedTools { get; set; } = "Write,Edit,NotebookEdit,Bash";

    /// <summary>
    /// Permission mode passed to the CLI. "manual" means nothing is auto-approved beyond the
    /// explicitly allowed tools, and there is no human in a headless run to approve anything else.
    /// Must not be "auto", "acceptEdits" or "bypassPermissions".
    /// </summary>
    public string PermissionMode { get; set; } = "manual";

    /// <summary>
    /// Run the CLI with <c>--restricted</c>, which makes it ignore the host user's, project and local
    /// settings files. Without this the service account's own <c>~/.claude/settings.json</c> applies -
    /// a <c>defaultMode</c> of "auto" there silently re-grants the write access this bridge withholds.
    /// </summary>
    public bool Restricted { get; set; } = true;

    /// <summary>
    /// Working directory for calls that carry no image. Under <c>--restricted</c> the file tools are
    /// confined to the working directory, so this deliberately points somewhere empty - the CLI must
    /// never be able to browse the reports folder and decide a receipt is already done.
    /// </summary>
    public string? ScratchDirectory { get; set; }

    /// <summary>
    /// The ONLY tools the bridge will ever pass to the host `claude` CLI. A caller-supplied tool list
    /// is intersected against this — the `claude` process runs un-sandboxed on the host, so allowing
    /// a request to add Bash/Write/Edit would be remote code execution. Read is needed for receipt
    /// images; WebSearch for price checks; WebFetch lets research callers (e.g. InfoGrid's area
    /// check) read official pages directly. Do not add execution/file-write tools here.
    /// </summary>
    public HashSet<string> ToolAllowlist { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Read", "WebSearch", "WebFetch",
    };
}
