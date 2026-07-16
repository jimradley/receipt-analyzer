using ReceiptAnalyzer.Bridge;

namespace ReceiptAnalyzer.Tests;

public class PathMapperTests
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/data/shopping"] = @"C:\AI\Projects\Shopping",
    };

    [Fact]
    public void Maps_a_container_path_under_the_configured_prefix()
    {
        var mapped = PathMapper.Map("/data/shopping/.state/bridge-tmp/abc123.jpg", Map);
        Assert.Equal(@"C:\AI\Projects\Shopping\.state\bridge-tmp\abc123.jpg", mapped);
    }

    [Fact]
    public void Maps_the_bare_prefix_to_the_host_base_path()
    {
        Assert.Equal(@"C:\AI\Projects\Shopping", PathMapper.Map("/data/shopping", Map));
    }

    [Fact]
    public void Leaves_an_unmatched_path_unchanged()
    {
        Assert.Equal(@"C:\already\a\host\path.jpg", PathMapper.Map(@"C:\already\a\host\path.jpg", Map));
    }

    [Fact]
    public void Is_case_insensitive_on_the_prefix()
    {
        var mapped = PathMapper.Map("/DATA/SHOPPING/receipt.jpg", Map);
        Assert.Equal(@"C:\AI\Projects\Shopping\receipt.jpg", mapped);
    }

    [Fact]
    public void Does_not_partial_match_a_similar_but_different_prefix()
    {
        // "/data/shoppingx" must not be treated as under "/data/shopping".
        var mapped = PathMapper.Map("/data/shoppingx/file.jpg", Map);
        Assert.Equal("/data/shoppingx/file.jpg", mapped);
    }

    [Fact]
    public void Preserves_posix_separators_for_a_posix_host_path()
    {
        var map = new Dictionary<string, string>
        {
            ["/data/shopping"] = "/srv/shopping",
        };

        var mapped = PathMapper.Map("/data/shopping/.state/receipt.jpg", map);

        Assert.Equal("/srv/shopping/.state/receipt.jpg", mapped);
    }
}
