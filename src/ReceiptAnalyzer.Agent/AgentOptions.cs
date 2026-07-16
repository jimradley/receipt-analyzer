namespace ReceiptAnalyzer.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string Provider { get; set; } = "claude";
    public string Model { get; set; } = "claude-sonnet-4-6";

    /// <summary>
    /// Optional model override for the price-check stage only (web-search quality is heavily
    /// model-dependent). Null falls back to <see cref="Model"/> for every stage.
    /// </summary>
    public string? PriceCheckModel { get; set; }

    public string ApiKeyEnvVar { get; set; } = "ANTHROPIC_API_KEY";
    public string? RulesFilePath { get; set; }

    /// <summary>Settings for the "claude-code" provider — a bridge to headless Claude Code on the host.</summary>
    public ClaudeCodeOptions ClaudeCode { get; set; } = new();
}

/// <summary>
/// Settings for calling headless Claude Code (<c>claude -p</c>) via the host-side bridge
/// (<c>ReceiptAnalyzer.Bridge</c>) instead of a hosted API — zero marginal cost under a Max
/// subscription. Selected when <see cref="AgentOptions.Provider"/> is "claude-code".
/// </summary>
public sealed class ClaudeCodeOptions
{
    public string BridgeUrl { get; set; } = "http://localhost:5095";

    /// <summary>Env var on THIS process holding the shared secret sent as the X-BRIDGE-KEY header.</summary>
    public string BridgeKeyEnvVar { get; set; } = "RECEIPT_BRIDGE_KEY";

    /// <summary>
    /// Model passed to <c>claude -p --model</c>. Configurable up to Fable — note Fable burns
    /// Max-plan usage faster than Sonnet.
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-5";

    /// <summary>Generous — price-check chunks with real web searches can run for minutes.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Directory (host-visible via the bridge's path map) for temp receipt images passed to
    /// <c>claude -p</c>'s Read tool. Null resolves to a folder under Reports:OutputDir at DI
    /// registration time; falls back to the machine temp folder if that isn't available either.
    /// </summary>
    public string? TmpDir { get; set; }
}
