using ReceiptAnalyzer.Bridge;

namespace ReceiptAnalyzer.Tests;

public class ClaudeLimitDetectorTests
{
    [Theory]
    [InlineData("You've hit your session limit · resets 1pm (Europe/London)")]
    [InlineData("You've hit your session limit Â· resets 1pm (Europe/London)")]
    [InlineData("HIT YOUR WEEKLY LIMIT")]
    [InlineData("usage limit reached; try again later")]
    public void Recognises_allowance_errors(string message) =>
        Assert.True(ClaudeLimitDetector.IsLimitError(message));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("authentication failed")]
    [InlineData("response contained no JSON object")]
    public void Does_not_treat_unrelated_failures_as_allowance_errors(string? message) =>
        Assert.False(ClaudeLimitDetector.IsLimitError(message));
}
