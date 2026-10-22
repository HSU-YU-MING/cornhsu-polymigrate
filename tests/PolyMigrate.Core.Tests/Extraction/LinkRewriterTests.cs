using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

public class LinkRewriterTests
{
    private static readonly SiteConfig Config = TestConfigs.IbpsLike();
    private static readonly LinkRewriter Rewriter = new(Config.Site, Config.UrlPattern);
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

    [Theory]
    // 解析後落在別的 host / 別的協定:不是站內頁,原樣保留(不可改寫成本站路由)
    [InlineData("ftp://files.ibps-austin.org/a.pdf")]
    // 解析不了的 href(UriFormatException):原樣保留,不得讓整頁抽取失敗
    [InlineData("http:[x")]
    [InlineData("ftp://[::1")]
    public void NonSiteOrMalformedHrefs_Untouched(string href) =>
        Assert.Equal(href, Rewriter.Rewrite(href, PageUrl));

    [Theory]
    // 第二輪審查:同 host 的非 http(s) 網址原本被當成站內路徑改寫(ftp://同站/ch/file.php → /ch/file)。
    // 只有 http(s) 才是網站的頁;其他 scheme 原樣保留
    [InlineData("ftp://www.ibps-austin.org/ch/file.php")]
    [InlineData("FTP://WWW.IBPS-AUSTIN.ORG/ch/file.php")]
    public void SameHostNonHttpScheme_Untouched(string href) =>
        Assert.Equal(href, Rewriter.Rewrite(href, PageUrl));

    [Theory]
    // 第二輪審查:只剩一段的路徑一律加尾斜線(「只剩語言前綴 → 該語首頁」),連帶把檔案也改壞了:
    // /embed.swf → /embed.swf/、/calendar.html → /calendar.html/。剝完 strip_extensions 還帶副檔名的
    // 是檔案,不是頁,不加尾斜線;語言首頁與無前綴的根層頁照舊
    [InlineData("/embed.swf", "/embed.swf")]
    [InlineData("/calendar.html", "/calendar.html")]
    [InlineData("/brochure.pdf", "/brochure.pdf")]
    [InlineData("/clip.mp4", "/clip.mp4")]
    [InlineData("/ch/index.php", "/ch/")]              // 語言首頁:照舊
    [InlineData("/about.php", "/about/")]              // 無語言前綴的根層頁:照舊(盤點 §3.7)
    [InlineData("/v1.2-release.php", "/v1.2-release/")]   // .2-release 不是副檔名:仍是頁
    [InlineData("/2019.05.01.php", "/2019.05.01/")]       // .01 不是副檔名
    public void SingleSegment_TrailingSlashOnlyForPages(string href, string expected) =>
        Assert.Equal(expected, Rewriter.Rewrite(href, PageUrl));

    [Theory]
    // 絕對 http(s) 連結一律原樣保留(只改寫相對連結),同站的也一樣;scheme 的大小寫不影響這個判斷。
    // 測資必須是「同站」:別站的網址就算前綴比對改成分大小寫,也會在後面的 host 檢查被原樣傳回,守不住前綴這一關
    [InlineData("https://www.ibps-austin.org/ch/news/a.php")]
    [InlineData("HTTPS://WWW.IBPS-AUSTIN.ORG/ch/news/a.php")]   // 前綴分大小寫的話會被改寫成 /ch/news/a
    public void AbsoluteHttpLinks_PassThrough_SchemeCaseInsensitive(string href) =>
        Assert.Equal(href, Rewriter.Rewrite(href, PageUrl));

    [Fact]
    public void SiteHostMatch_IsCaseInsensitive()
    {
        // 主機名本來就不分大小寫;舊站內文裡大小寫混用的絕對連結仍是站內連結
        Assert.Equal("/ch/news/a", Rewriter.RouteForUrl("https://WWW.IBPS-AUSTIN.ORG/ch/news/a.php"));
        Assert.Null(Rewriter.RouteForUrl("https://en.ibps-austin.org/ch/news/a.php"));   // 子網域是別的站
        Assert.Null(Rewriter.RouteForUrl("not a url"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NullOrEmptyHref_YieldsEmpty(string? href, string expected) =>
        Assert.Equal(expected, Rewriter.Rewrite(href, PageUrl));

    [Theory]
    // query 與 fragment 是頁面身分的一部分,改寫時不可丟(?id=5 兩頁不能塌成同一路由)
    [InlineData("detail.php?id=5", "/ch/news/detail?id=5")]
    [InlineData("../about.php#team", "/ch/about#team")]
    [InlineData("list.php?page=2#top", "/ch/news/list?page=2#top")]
    public void QueryAndFragment_Preserved(string href, string expected) =>
        Assert.Equal(expected, Rewriter.Rewrite(href, PageUrl));
}
