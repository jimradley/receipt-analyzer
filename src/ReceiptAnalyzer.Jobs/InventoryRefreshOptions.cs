namespace ReceiptAnalyzer.Jobs;

public sealed class InventoryRefreshOptions
{
    public int KnownBatchSize { get; init; } = 50;
    public int AgentBatchSize { get; init; } = 5;
    public int RecentPurchaseDays { get; init; } = 90;
    public int RecentRefreshDays { get; init; } = 7;
    public int OlderRefreshDays { get; init; } = 30;
    public int UnmatchedRetryDays { get; init; } = 30;
    public TimeSpan BatchDelay { get; init; } = TimeSpan.FromHours(1);
    public string BridgeUrl { get; init; } = "http://localhost:5095";
    public string BridgeKeyEnvVar { get; init; } = "RECEIPT_BRIDGE_KEY";
    public string CodexModel { get; init; } = "gpt-5.6-luna";
}
