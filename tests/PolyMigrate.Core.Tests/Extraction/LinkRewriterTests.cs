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

    // ─────────────────────────────────────────────────────────────────────────
    // 第三輪審查:頁的路由必須與 9d17071 逐位元相同。RouteForPath(redirect_map、RouteToKey、hreflang)
    // 對任何輸入都不套用「檔案」判斷;只有改寫連結時、而且目標不是鏡像頁,才考慮它。
    // ─────────────────────────────────────────────────────────────────────────

    private static LinkRewriter RewriterFor(string langMap, params string[] mirrorRoutes)
    {
        var config = TestConfigs.IbpsLike();
        config.UrlPattern.LangMap = langMap.Split(',').ToDictionary(k => k, _ => "zh-Hant");
        return new LinkRewriter(config.Site, config.UrlPattern, mirrorRoutes.ToHashSet(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/jane.doe.php", "/jane.doe/")]
    [InlineData("/node.js.php", "/node.js/")]
    [InlineData("/asp.net.php", "/asp.net/")]
    [InlineData("/v2.0.release.php", "/v2.0.release/")]
    [InlineData("/index.en.php", "/index.en/")]
    [InlineData("/2026.q1.php", "/2026.q1/")]
    [InlineData("/CONTACT.PHP", "/CONTACT.PHP/")]          // 大小寫與 strip_extensions 不符:沒剝,仍是頁
    [InlineData("/calendar.html", "/calendar.html/")]      // 鏡像裡的 calendar.html 頁
    [InlineData("/jane.doe", "/jane.doe/")]                // 乾淨網址站
    [InlineData("/zh.tw/index.php", "/zh.tw/")]            // key 帶點的語言首頁
    [InlineData("/node.js/", "/node.js/")]
    [InlineData("/about.php", "/about/")]
    [InlineData("/ch/index.php", "/ch/")]
    [InlineData("/ch/news/x.y.php", "/ch/news/x.y")]
    public void PageRoutes_AreExactlyThe9d17071Rule(string path, string route) =>
        Assert.Equal(route, Rewriter.RouteForPath(path));

    [Fact]
    public void RouteForUrl_IgnoresTheScheme_LikeBefore()
    {
        // hreflang 的目標路由(hreflang_map.csv)屬於「頁的路由」,不可因為連結改寫的 scheme 判斷而改變
        Assert.Equal("/ch/news/a", Rewriter.RouteForUrl("ftp://www.ibps-austin.org/ch/news/a.php"));
    }

    [Theory]
    // 目標是鏡像頁:一律照頁的規則(與 9d17071 相同),不管它長得像不像檔案
    [InlineData("/calendar.html", "/calendar.html/")]
    [InlineData("/jane.doe", "/jane.doe/")]
    [InlineData("ftp://www.ibps-austin.org/calendar.html", "/calendar.html/")]   // 同 host 的非 http(s),指到鏡像頁:照舊改寫
    public void LinkToAMirrorPage_FollowsThePageRule(string href, string expected) =>
        Assert.Equal(expected, RewriterFor("ch", "/calendar.html/", "/jane.doe/").Rewrite(href, PageUrl));

    [Theory]
    // 目標不是鏡像頁時才考慮「檔案」:剝了 strip_extensions、拿掉了 index、原本以 / 結尾、或是語言前綴的,仍當頁
    [InlineData("/jane.doe.php", "/jane.doe/")]
    [InlineData("/node.js/", "/node.js/")]
    [InlineData("/zh.tw", "/zh.tw/")]
    [InlineData("/zh.tw/index.php", "/zh.tw/")]
    [InlineData("/embed.swf", "/embed.swf")]
    [InlineData("/brochure.pdf", "/brochure.pdf")]
    [InlineData("/v1.2-rc", "/v1.2-rc/")]                   // .2-rc 有非英數字:不是副檔名(第三輪 R4)
    [InlineData("/site.webmanifest", "/site.webmanifest")]  // 11 字的副檔名(R5)
    [InlineData("/a.abcdefghijklmnopq", "/a.abcdefghijklmnopq/")]   // 17 字:太長,不當副檔名(R5)
    [InlineData("/.htaccess", "/.htaccess")]                // 點檔案(R6)
    [InlineData("ftp://www.ibps-austin.org/ch/file.php", "ftp://www.ibps-austin.org/ch/file.php")]
    public void LinkToANonMirrorTarget_MayBeAFile(string href, string expected) =>
        Assert.Equal(expected, RewriterFor("ch,zh.tw", "/calendar.html/").Rewrite(href, PageUrl));

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
