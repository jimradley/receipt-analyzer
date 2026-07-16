namespace ReceiptAnalyzer.Bridge;

/// <summary>
/// Decides which tools the bridge will pass to the host <c>claude</c> CLI. The CLI runs un-sandboxed
/// on the host, so a caller-supplied tool list is never trusted: it is intersected against the
/// server's <see cref="BridgeOptions.ToolAllowlist"/>. This prevents a compromised (internet-exposed)
/// container or a LAN keyholder from requesting Bash/Write/Edit and turning the bridge into host RCE.
/// </summary>
public static class ToolFilter
{
    /// <summary>
    /// Returns the permitted tools for a request. <paramref name="requested"/> null/empty falls back to
    /// <see cref="BridgeOptions.DefaultAllowedTools"/>; anything not on the allowlist is dropped. When
    /// <paramref name="needsRead"/> is true (an image path is present) Read is added if the allowlist
    /// permits it.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string>? requested, BridgeOptions options, bool needsRead)
    {
        var source = requested?.ToList() is { Count: > 0 } list
            ? list
            : options.DefaultAllowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var permitted = source
            .Select(t => t.Trim())
            .Where(t => options.ToolAllowlist.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (needsRead &&
            !permitted.Contains("Read", StringComparer.OrdinalIgnoreCase) &&
            options.ToolAllowlist.Contains("Read"))
            permitted.Add("Read");

        return permitted;
    }
}
