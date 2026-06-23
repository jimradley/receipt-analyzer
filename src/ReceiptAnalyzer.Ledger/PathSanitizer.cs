namespace ReceiptAnalyzer.Ledger;

public static class PathSanitizer
{
    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        
        // Remove invalid chars and directory navigation
        var sanitized = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        sanitized = sanitized.Replace("..", "").Replace("/", "").Replace("\\", "");
        
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    public static string EnsureSafePath(string root, string subPath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, subPath));
        var rootPath = Path.GetFullPath(root);

        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Attempted to access path outside of the allowed root directory.");
        }

        return fullPath;
    }
}
