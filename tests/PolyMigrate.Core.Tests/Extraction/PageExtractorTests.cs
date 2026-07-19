using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

public class PageExtractorTests : IDisposable
{
    private readonly string _mediaRoot = Directory.CreateTempSubdirectory("polymigrate-media").FullName;
    private readonly PageExtractor _extractor = new(TestConfigs.IbpsLike());

    private static readonly RawPage ArticlePage = new(
        FilePath: "unused",
        LangPrefix: "ch",
        Locale: "zh-Hant",
        Section: "news",
        Slug: "20260712",
        SourceUrl: "https://www.ibps-austin.org/ch/news/20260712.php",
        TranslationKey: "news/20260712");

    public void Dispose() => Directory.Delete(_mediaRoot, recursive: true);

    private void AddMediaFile(string relative)
    {
        var path = Path.Combine(_mediaRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private const string ArticleHtml =
        """
        <html><head><title>佛光山香雲寺 2026/07/12 新聞:供僧法會</title></head><body>
        <section id="header_main">nav</section>
        <section id="personal"><div class="personal_1">
          <h3>供僧法會圓滿</h3><h5>2026/07/12</h5>
          <p>大眾參與 <a href="../support/donate.php">護持</a>。</p>
          <iframe src="https://www.youtube.com/embed/abc123"></iframe>
          <ol>
            <li><a href="x"><img src="images/20260712_1.jpg" alt="法會"></a></li>
            <li><img src="images/20260712_9.jpg" alt="缺圖"></li>
          </ol>
          <a href="#carousel-example-generic"></a>
        </div></section>
        <section id="footer">footer</section>
        </body></html>
        """;

    [Fact]
    public void Article_FullExtraction()
    {
        AddMediaFile("ch/news/images/20260712_1.jpg");

        var result = _extractor.Extract(ArticlePage, ArticleHtml, _mediaRoot);

        // 標題:內文 h3 優先,非 <title>
        Assert.Equal("供僧法會圓滿", result.Title);
        Assert.Equal("article", result.PageType);

        // 正文:框架 section 不進來、重複的 h3/h5 已移除、連結已改寫
        Assert.DoesNotContain("nav", result.BodyMarkdown);
        Assert.DoesNotContain("footer", result.BodyMarkdown);
        Assert.DoesNotContain("供僧法會圓滿", result.BodyMarkdown);
        Assert.Contains("[護持](/ch/support/donate)", result.BodyMarkdown);

        // 影片:佔位符已還原為內嵌 HTML,保留在原位置(§2.6)
        Assert.Contains("youtube.com/embed/abc123", result.BodyMarkdown);
        Assert.DoesNotContain("@@EMBED", result.BodyMarkdown);
        Assert.Equal("youtube", Assert.Single(result.Videos)["type"]);

        // 相簿頁型:存在的圖進 frontmatter images、內文移除;壞圖記錄不入相簿(§2.6)
        var image = Assert.Single(result.Images);
        Assert.Equal("/media/ch/news/images/20260712_1.jpg", image.Web);
        Assert.DoesNotContain("![", result.BodyMarkdown);
        var missing = Assert.Single(result.MissingImages);
        Assert.Equal("/media/ch/news/images/20260712_9.jpg", missing.WebPath);

        // 相簿圖移除後的空 <li> 與輪播空連結不殘留(對齊 markdownify 行為)
        Assert.DoesNotContain(result.BodyMarkdown.Split('\n'),
            l => l.TrimEnd() is "1." or "2." or "-");
        Assert.DoesNotContain("[](", result.BodyMarkdown);
    }

    [Fact]
    public void NonGalleryPage_KeepsInlineImage_WithSingleEncodedUrl()
    {
        AddMediaFile("images/a b.jpg");
        var page = ArticlePage with { Section = "austin", TranslationKey = "austin/intro", Slug = "intro" };
        const string html =
            """
            <html><body><section id="intro">
            <p>介紹</p><img src="/images/a%20b.jpg" alt="外觀">
            </section></body></html>
            """;

        var result = _extractor.Extract(page, html, _mediaRoot);

        // §2.6:磁碟存解碼名、URL 單次編碼
        Assert.Contains("![外觀](/media/images/a%20b.jpg)", result.BodyMarkdown);
        Assert.Empty(result.MissingImages);
        Assert.Equal("images/a b.jpg", Assert.Single(result.MediaUses).MediaRelative);
    }

    [Fact]
    public void NoSelectorMatch_FallsBackToBody()
    {
        var page = ArticlePage with { Section = "", Slug = "index", TranslationKey = "index" };

        var result = _extractor.Extract(page, "<html><body><p>hello world</p></body></html>", _mediaRoot);

        Assert.Contains("hello world", result.BodyMarkdown);
    }

    [Fact]
    public void PdfIframe_BecomesEmbedWithLocaleLabel()
    {
        const string html =
            """
            <html><body><section id="personal">
            <iframe src="/files/schedule.pdf"></iframe>
            </section></body></html>
            """;

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        Assert.Contains("下載／檢視 PDF", result.BodyMarkdown);
        Assert.Equal("/media/files/schedule.pdf", Assert.Single(result.Documents)["src"]);
        Assert.Equal("https://www.ibps-austin.org/files/schedule.pdf", Assert.Single(result.NeedFetch));
    }

    [Fact]
    public void ShortTextWithImage_FlagsTextInImage()
    {
        AddMediaFile("img/poster.jpg");
        var page = ArticlePage with { Section = "events", Slug = "2026_cjgx", TranslationKey = "events/2026_cjgx" };
        const string html =
            """
            <html><body><section id="event"><img src="/img/poster.jpg" alt=""></section></body></html>
            """;

        var result = _extractor.Extract(page, html, _mediaRoot);

        Assert.Equal("event", result.PageType);
        Assert.Contains("text_in_image", result.Flags);
    }
}
