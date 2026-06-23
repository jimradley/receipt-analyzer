using ReceiptAnalyzer.Ledger;

namespace ReceiptAnalyzer.Tests;

public class PathSanitizerTests
{
    [Fact]
    public void SanitizeFolderName_strips_traversal_and_separators()
    {
        var result = PathSanitizer.SanitizeFolderName("../../etc/passwd");
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public void SanitizeFolderName_falls_back_to_unknown(string input)
    {
        Assert.Equal("unknown", PathSanitizer.SanitizeFolderName(input));
    }

    [Fact]
    public void EnsureSafePath_returns_combined_path_inside_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "ra-test-root");
        var result = PathSanitizer.EnsureSafePath(root, "report.md");
        Assert.StartsWith(Path.GetFullPath(root), result);
        Assert.EndsWith("report.md", result);
    }

    [Fact]
    public void EnsureSafePath_throws_when_escaping_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "ra-test-root");
        Assert.Throws<UnauthorizedAccessException>(
            () => PathSanitizer.EnsureSafePath(root, Path.Combine("..", "..", "evil.md")));
    }
}
