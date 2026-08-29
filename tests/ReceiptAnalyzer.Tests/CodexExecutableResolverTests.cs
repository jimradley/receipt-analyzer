using ReceiptAnalyzer.Bridge;

namespace ReceiptAnalyzer.Tests;

public class CodexExecutableResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codex-resolver-{Guid.NewGuid():N}");

    [Fact]
    public void Finds_newest_codex_desktop_native_executable()
    {
        var older = CreateExecutable("older", DateTime.UtcNow.AddDays(-1));
        var newer = CreateExecutable("newer", DateTime.UtcNow);

        var resolved = CodexExecutableResolver.Resolve("codex.exe", _root);

        Assert.Equal(newer, resolved);
        Assert.NotEqual(older, resolved);
    }

    [Fact]
    public void Preserves_explicit_executable_path()
    {
        const string configured = @"D:\Tools\codex.exe";

        Assert.Equal(configured, CodexExecutableResolver.Resolve(configured, _root));
    }

    [Fact]
    public void Falls_back_to_configured_name_when_desktop_cli_is_absent() =>
        Assert.Equal("codex.exe", CodexExecutableResolver.Resolve("codex.exe", _root));

    private string CreateExecutable(string version, DateTime modifiedUtc)
    {
        var path = Path.Combine(_root, "OpenAI", "Codex", "bin", version, "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
