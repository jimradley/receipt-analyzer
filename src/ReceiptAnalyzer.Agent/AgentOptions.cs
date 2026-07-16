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
}
