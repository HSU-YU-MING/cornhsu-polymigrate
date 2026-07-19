using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

public class LinkRewriterTests
{
    private static readonly LinkRewriter Rewriter = new(TestConfigs.IbpsLike());
    private const string PageUrl = "https://www.ibps-austin.org/ch/news/20260712.php";

    [Theory]
    // 站內 .php 相對連結 → 新路由;../ 用 URL 解析正確處理(§2.6)
    [InlineData("../support/donate.php", "/ch/support/donate")]
    [InlineData("20250101.php", "/ch/news/20250101")]
    [InlineData("/en/events/index.php", "/en/events")]
    [InlineData("../../en/index.php", "/en/")]
    public void InternalDynamicLinks_RewrittenToNewRoutes(string href, string expected) =>
        Assert.Equal(expected, Rewriter.Rewrite(href, PageUrl));

    [Theory]
    // 外部/錨點/協定連結保留原樣
    [InlineData("https://www.fgs.org.tw/")]
    [InlineData("#top")]
    [InlineData("mailto:info@ibps-austin.org")]
    [InlineData("tel:+15125555555")]
    [InlineData("//cdn.example.com/x.js")]
    [InlineData("javascript:void(0)")]
    public void ExternalAndSpecialLinks_Untouched(string href) =>
        Assert.Equal(href, Rewriter.Rewrite(href, PageUrl));

    [Fact]
    public void RootIndex_CollapsesToSlash() =>
        Assert.Equal("/", Rewriter.Rewrite("/index.php", PageUrl));
}
