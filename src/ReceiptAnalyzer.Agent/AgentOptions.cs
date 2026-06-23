namespace ReceiptAnalyzer.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string Provider { get; set; } = "claude";
    public string Model { get; set; } = "claude-sonnet-4-6";
    public string ApiKeyEnvVar { get; set; } = "ANTHROPIC_API_KEY";
    public string? RulesFilePath { get; set; }
}
