using ReceiptAnalyzer.Bridge;

namespace ReceiptAnalyzer.Tests;

public class ToolFilterTests
{
    private static BridgeOptions Options() => new(); // default allowlist: Read, WebSearch

    [Fact]
    public void Drops_dangerous_caller_supplied_tools()
    {
        // The core RCE guard: a caller asking for Bash gets nothing dangerous back.
        var permitted = ToolFilter.Resolve(new[] { "Bash", "Write", "Edit" }, Options(), needsRead: false);
        Assert.Empty(permitted);
    }

    [Fact]
    public void Keeps_only_allowlisted_tools_from_a_mixed_request()
    {
        var permitted = ToolFilter.Resolve(new[] { "WebSearch", "Bash" }, Options(), needsRead: false);
        Assert.Equal(new[] { "WebSearch" }, permitted);
    }

    [Fact]
    public void Falls_back_to_the_default_set_when_none_requested()
    {
        var permitted = ToolFilter.Resolve(null, Options(), needsRead: false);
        Assert.Contains("Read", permitted);
        Assert.Contains("WebSearch", permitted);
    }

    [Fact]
    public void Adds_read_when_an_image_is_present()
    {
        var permitted = ToolFilter.Resolve(new[] { "WebSearch" }, Options(), needsRead: true);
        Assert.Contains("Read", permitted);
    }

    [Fact]
    public void Does_not_add_read_when_the_allowlist_forbids_it()
    {
        var options = new BridgeOptions { ToolAllowlist = new(StringComparer.OrdinalIgnoreCase) { "WebSearch" } };
        var permitted = ToolFilter.Resolve(new[] { "WebSearch" }, options, needsRead: true);
        Assert.DoesNotContain("Read", permitted);
    }

    [Fact]
    public void Is_case_insensitive_and_deduplicates()
    {
        var permitted = ToolFilter.Resolve(new[] { "read", "READ", "webSEARCH" }, Options(), needsRead: false);
        Assert.Equal(2, permitted.Count);
    }
}
