using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Tests.Inventory;

/// <summary>InventoryAggregator 是純記憶體聚合,不落磁碟即可測(D1:把摺疊邏輯自 pipeline 抽出)。</summary>
public class InventoryAggregatorTests
{
    private static readonly SiteConfig Config = TestConfigs.IbpsLike();

    private static InventoryAggregator NewAggregator() =>
        new(new LinkRewriter(Config.Site, Config.UrlPattern));

    private static RawPage Page(string lang, string locale, string slug) =>
        new($"C:/raw/{lang}/news/{slug}.php.html", lang, locale, "news", slug,
            $"https://www.ibps-austin.org/{lang}/news/{slug}.php", $"news/{slug}");

    private static ExtractedPage Extracted(RawPage page, int textLen, string[] flags, MediaUse[] media) => new()
    {
        Page = page,
        Title = page.Slug,
        PageType = "article",
        Flags = [.. flags],
        TextLength = textLen,
        ImageCount = media.Length,
        BodyMarkdown = "",
        Images = [],
        Videos = [],
        Documents = [],
        MediaUses = [.. media],
        MissingImages = [],
        NeedFetch = [],
        Alternates = [],
    };

    [Fact]
    public void TwoLocales_FoldIntoOneTranslationKey()
    {
        var agg = NewAggregator();
        var ch = Page("ch", "zh-Hant", "a");
        var en = Page("en", "en", "a");
        var shared = new MediaUse("news/a/img.jpg", "https://www.ibps-austin.org/media/news/a/img.jpg", ch.SourceUrl, "圖");

        agg.Add(ch, Extracted(ch, 100, ["text_in_image"], [shared]));
        agg.Add(en, Extracted(en, 200, [], [shared with { SourceUrl = en.SourceUrl }]));

        var record = Assert.Single(agg.Inventory).Value;
        Assert.Equal(new HashSet<string> { "zh-Hant", "en" }, record.TextByLocale.Keys.ToHashSet());
        Assert.Equal(100, record.TextByLocale["zh-Hant"]);
        Assert.Equal(200, record.TextByLocale["en"]);
        Assert.Contains("text_in_image", record.Flags);          // flag 各語版聯集
        Assert.Single(record.Media);                             // 同一張圖只算一次

        // media_manifest 端:同一媒體被兩個語言版引用 → 兩筆 referenced_by
        var entry = Assert.Single(agg.MediaRefs).Value;
        Assert.Equal(2, entry.Refs.Count);
        Assert.Contains("圖", entry.Alts);
    }

    [Fact]
    public void EmptyAlt_IsNotListedInTheManifestAlts()
    {
        // contracts.md:media_manifest 的 alt 欄只收「非空的」alt 文字。一語版有 alt、另一語版留空時,
        // 不可寫出「 | 圖」這種前面帶一格空值的欄位
        var agg = NewAggregator();
        var ch = Page("ch", "zh-Hant", "a");
        var en = Page("en", "en", "a");
        const string Url = "https://www.ibps-austin.org/media/news/a/img.jpg";

        agg.Add(ch, Extracted(ch, 10, [], [new MediaUse("news/a/img.jpg", Url, ch.SourceUrl, "圖")]));
        agg.Add(en, Extracted(en, 10, [], [new MediaUse("news/a/img.jpg", Url, en.SourceUrl, "")]));

        var entry = Assert.Single(agg.MediaRefs).Value;
        Assert.Equal(["圖"], entry.Alts);
        Assert.Equal([ch.SourceUrl, en.SourceUrl], entry.Refs);
    }

    [Fact]
    public void ImageCount_IsTheMaximumAcrossLocales()
    {
        // contracts.md:content_inventory 的 image_count「各語版取最大」。
        // 後處理的語版圖比較多:先到先贏或後到蓋前都會算錯(golden 的那一組剛好是前面的多)
        var agg = NewAggregator();
        var ch = Page("ch", "zh-Hant", "a");
        var en = Page("en", "en", "a");
        MediaUse Use(RawPage p, int i) => new($"news/a/{i}.jpg", $"https://www.ibps-austin.org/news/a/{i}.jpg", p.SourceUrl, "");

        agg.Add(ch, Extracted(ch, 10, [], [Use(ch, 1)]));
        agg.Add(en, Extracted(en, 10, [], [Use(en, 1), Use(en, 2), Use(en, 3)]));

        Assert.Equal(3, Assert.Single(agg.Inventory).Value.ImageCount);
    }

    [Fact]
    public void Redirect_NewPathDerivedFromSourceUrlRoute()
    {
        var agg = NewAggregator();
        var ch = Page("ch", "zh-Hant", "20260101");
        agg.Add(ch, Extracted(ch, 10, [], []));

        var redirect = Assert.Single(agg.Redirects);
        Assert.Equal("https://www.ibps-austin.org/ch/news/20260101.php", redirect.OldUrl);
        Assert.Equal("/ch/news/20260101", redirect.NewPath);     // .php 去尾綴後的新路由
    }
}
