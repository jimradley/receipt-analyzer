namespace ReceiptAnalyzer.Bridge;

/// <summary>
/// Translates a container-visible path to its host equivalent using configured prefix pairs, so the
/// host-side <c>claude</c> CLI's Read tool can find a file the calling (containerised) API wrote to a
/// shared bind mount. Pure and I/O-free, so it's unit-testable without spawning any process.
/// </summary>
public static class PathMapper
{
    /// <summary>
    /// Maps <paramref name="path"/> using the first entry in <paramref name="pathMap"/> whose
    /// container prefix matches (case-insensitively, tolerant of slash direction). Returns
    /// <paramref name="path"/> unchanged if no prefix matches — e.g. it's already a host path
    /// (typical in local dev, where the API and bridge share a filesystem).
    /// </summary>
    public static string Map(string path, IReadOnlyDictionary<string, string> pathMap)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var normalized = path.Replace('\\', '/');
        foreach (var (containerPrefix, hostPrefix) in pathMap)
        {
            var prefix = containerPrefix.Replace('\\', '/').TrimEnd('/');
            if (prefix.Length == 0) continue;

            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rest = normalized.Length > prefix.Length ? normalized[(prefix.Length + 1)..] : "";
                var hostBase = hostPrefix.TrimEnd('\\', '/');
                return rest.Length == 0
                    ? hostBase
                    : Path.Combine(hostBase, rest.Replace('/', Path.DirectorySeparatorChar));
            }
        }
        return path;
    }
}
