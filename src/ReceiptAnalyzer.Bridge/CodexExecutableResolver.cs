namespace ReceiptAnalyzer.Bridge;

/// <summary>Finds the native Codex Desktop CLI when a Windows service has a restricted PATH.</summary>
public static class CodexExecutableResolver
{
    public static string Resolve(string configured, string? localAppData = null)
    {
        if (!string.Equals(configured, "codex", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(configured, "codex.exe", StringComparison.OrdinalIgnoreCase))
            return configured;

        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            if (!Directory.Exists(binRoot))
                return configured;

            return Directory.EnumerateDirectories(binRoot)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? configured;
        }
        catch (IOException)
        {
            return configured;
        }
        catch (UnauthorizedAccessException)
        {
            return configured;
        }
    }
}
