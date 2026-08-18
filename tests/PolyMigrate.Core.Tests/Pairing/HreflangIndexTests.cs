using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Inventory;
using PolyMigrate.Core.Pairing;

namespace PolyMigrate.Core.Tests.Pairing;

/// <summary>
/// hreflang 的四道門(§1.4)。這些測試守的是同一件事:**宣告不等於正確**。
/// 舊站的 hreflang 多半是後來 SEO 外包加的,壞掉的比對的多,而壞掉的宣告若被當成
/// 權威訊號,會把兩篇無關的文章安靜地配在一起——那比誠實回報 missing 糟得多。
/// </summary>
public class HreflangIndexTests
{
    private static readonly SiteConfig Config = TestConfigs.IbpsLike();
    private const string Base = "https://www.ibps-austin.org";

    private static readonly LinkRewriter Links = new(Config.Site, Config.UrlPattern);

    /// <summary>用真的 LinkRewriter 算路由,與 pipeline 走同一套規則(否則測的是假的)。</summary>
    private static HreflangLink Link(string sourcePath, string sourceKey, string hreflang, string targetUrl)
    {
        var sourceUrl = Base + sourcePath;
        return new HreflangLink(
            sourceUrl, Links.RouteForPath(new Uri(sourceUrl).AbsolutePath), sourceKey,
            hreflang, targetUrl, Links.RouteForUrl(targetUrl));
    }

    private static Dictionary<string, string> Mirror(params (string Path, string Key)[] pages) =>
        pages.ToDictionary(
            p => Links.RouteForPath(p.Path), p => p.Key, StringComparer.Ordinal);

    [Fact]
    public void ReciprocalLinks_AreUsable_AndMarkedReciprocal()
    {
        var index = HreflangIndex.Build(
        [
            Link("/ch/events/a.php", "events/a", "en", $"{Base}/en/events/b.php"),
            Link("/en/events/b.php", "events/b", "zh-Hant", $"{Base}/ch/events/a.php"),
        ],
        Mirror(("/ch/events/a.php", "events/a"), ("/en/events/b.php", "events/b")));

        Assert.Equal(2, index.Declared);
        Assert.Equal(2, index.Usable);
        Assert.All(index.Rows, r => Assert.True(r.Reciprocal));
        Assert.Equal("en", index.AlternatesByKey["events/a"]["events/b"]);
        Assert.Equal("zh-Hant", index.AlternatesByKey["events/b"]["events/a"]);
    }

    [Fact]
    public void OneWayLink_IsStillUsable_ButNotReciprocal()
    {
        // 只有預設語言那半宣告 hreflang 在真實站上太常見,不能當成不可信。
        // 互指是加分(記在 reciprocal 欄),不是門檻——這一層只建議、不合併。
        var index = HreflangIndex.Build(
            [Link("/ch/events/a.php", "events/a", "en", $"{Base}/en/events/b.php")],
            Mirror(("/ch/events/a.php", "events/a"), ("/en/events/b.php", "events/b")));

        var row = Assert.Single(index.Rows);
        Assert.True(row.Usable);
        Assert.False(row.Reciprocal);
    }

    [Fact]
    public void TargetNotInMirror_IsRecordedButUnusable()
    {
        // 舊站最常見的壞法:hreflang 指到早就下架的 URL
        var index = HreflangIndex.Build(
            [Link("/ch/news/a.php", "news/a", "en", $"{Base}/en/news/gone.php")],
            Mirror(("/ch/news/a.php", "news/a")));

        var row = Assert.Single(index.Rows);
        Assert.False(row.InMirror);
        Assert.False(row.Usable);
        Assert.Equal("", row.TargetKey);
        Assert.Empty(index.AlternatesByKey);
    }

    [Fact]
    public void OffHostTarget_NeverMatchesASamePathPage()
    {
        // en.example.org/ch/news/a.php 的路徑與本站 /ch/news/a 完全相同——
        // 只比路徑的話,這一頁會變成自己的翻譯
        var index = HreflangIndex.Build(
            [Link("/ch/news/a.php", "news/a", "en", "https://en.other-host.org/ch/news/a.php")],
            Mirror(("/ch/news/a.php", "news/a")));

        Assert.False(Assert.Single(index.Rows).InMirror);
        Assert.Empty(index.AlternatesByKey);
    }

    [Fact]
    public void XDefaultAndSelfReference_AreRecordedButUnusable()
    {
        var index = HreflangIndex.Build(
        [
            Link("/ch/news/a.php", "news/a", "zh-Hant", $"{Base}/ch/news/a.php"),   // 自我指涉(標準做法)
            Link("/ch/news/a.php", "news/a", "x-default", $"{Base}/index.php"),      // 語言選擇頁
        ],
        Mirror(("/ch/news/a.php", "news/a"), ("/index.php", "/index")));

        Assert.Equal(2, index.Declared);
        Assert.Equal(0, index.Usable);
        Assert.All(index.Rows, r => Assert.True(r.InMirror));   // 兩個目標都在鏡像裡,只是配對上零資訊
        Assert.Empty(index.AlternatesByKey);
    }

    [Fact]
    public void DifferentUrlSpellings_OfTheSamePage_CountAsReciprocal()
    {
        // /en/events/b.php、/en/events/b、相對路徑——三種寫法都是同一頁
        var index = HreflangIndex.Build(
        [
            Link("/ch/events/a.php", "events/a", "en", $"{Base}/en/events/b"),
            Link("/en/events/b.php", "events/b", "zh-Hant", $"{Base}/ch/events/a.php"),
        ],
        Mirror(("/ch/events/a.php", "events/a"), ("/en/events/b.php", "events/b")));

        Assert.All(index.Rows, r => Assert.True(r.Reciprocal));
        Assert.Equal(2, index.Usable);
    }

    [Fact]
    public void MultipleHreflangsToSameKey_KeepsLowestOrdinal_ForDeterministicOutput()
    {
        var index = HreflangIndex.Build(
        [
            Link("/ch/news/a.php", "news/a", "en-US", $"{Base}/en/news/b.php"),
            Link("/ch/news/a.php", "news/a", "en", $"{Base}/en/news/b.php"),
        ],
        Mirror(("/ch/news/a.php", "news/a"), ("/en/news/b.php", "news/b")));

        Assert.Equal("en", index.AlternatesByKey["news/a"]["news/b"]);
    }
}
