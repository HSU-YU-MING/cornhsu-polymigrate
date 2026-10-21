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
    public void BodyTitle_StartingWithTheSiteName_IsKeptWhole()
    {
        // 站名雜訊只在退回 <title> 時剝:內文標題(title_selector 抓到的)可能合法地以站名開頭。
        // TitleCleanerTests 證明清理器分得開兩種模式;這裡證明抽取時真的用對了模式
        const string html =
            """
            <html><head><title>佛光山香雲寺 2026/07/12 新聞:別的標題</title></head><body>
            <section id="personal"><h3>Fo Guang Shan Xiang Yun Temple Hosts Winter Retreat</h3><p>內文</p></section>
            </body></html>
            """;

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        Assert.Equal("Fo Guang Shan Xiang Yun Temple Hosts Winter Retreat", result.Title);
    }

    [Fact]
    public void EmptyAltInlineImage_SurvivesTheEmptyLinkCleanup()
    {
        // CleanupMarkdown 會刪掉無文字連結 [](x)(輪播控制鈕),但 alt 留空的圖 ![](x) 長得只差一個驚嘆號,
        // 不可被一起刪掉——舊站的圖大半沒寫 alt
        AddMediaFile("images/a.jpg");
        var page = ArticlePage with { Section = "austin", TranslationKey = "austin/intro", Slug = "intro" };
        const string html =
            """
            <html><body><section id="intro"><p>這頁的文字夠長,不會被判成資訊鎖在圖裡的那一類頁面。</p>
            <img src="/images/a.jpg" alt=""><a href="#carousel"></a></section></body></html>
            """;

        var result = _extractor.Extract(page, html, _mediaRoot);

        Assert.Contains("![](/media/images/a.jpg)", result.BodyMarkdown);
        Assert.DoesNotContain("[](#carousel)", result.BodyMarkdown);
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
    public void LocalVideo_BecomesEmbed_FrontmatterLocalEntry_AndNeedFetch()
    {
        // 爬蟲當初沒抓 <source>,所以本地影片一律進待補清單;frontmatter 記 {type: local, src}
        const string html =
            """
            <html><body><section id="personal">
            <p>開場</p>
            <video controls><source src="clips/opening.mp4" type="video/mp4"></video>
            </section></body></html>
            """;

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        var video = Assert.Single(result.Videos);
        Assert.Equal(new Dictionary<string, string> { ["type"] = "local", ["src"] = "/media/ch/news/clips/opening.mp4" },
            video);
        Assert.Equal("https://www.ibps-austin.org/ch/news/clips/opening.mp4", Assert.Single(result.NeedFetch));
        Assert.Contains("<video controls preload=\"metadata\" src=\"/media/ch/news/clips/opening.mp4\"", result.BodyMarkdown);
        Assert.DoesNotContain("@@EMBED", result.BodyMarkdown);
        Assert.DoesNotContain("type=\"video/mp4\"", result.BodyMarkdown);   // 原 <video> 已整個換掉
    }

    [Theory]
    [InlineData("/files/schedule.PDF", "/media/files/schedule.PDF")]            // 副檔名大寫
    [InlineData("/files/schedule.pdf?dl=1", "/media/files/schedule.pdf")]      // 帶 query:網路路徑不含 query
    public void PdfIframe_RecognizedRegardlessOfCaseOrQuery(string src, string web)
    {
        var html = $"<html><body><section id=\"personal\"><iframe src=\"{src}\"></iframe></section></body></html>";

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        Assert.Equal(web, Assert.Single(result.Documents)["src"]);
        Assert.Equal("https://www.ibps-austin.org" + src, Assert.Single(result.NeedFetch));   // 待補清單保留原網址
        Assert.Contains($"<iframe src=\"{web}\"", result.BodyMarkdown);
    }

    [Theory]
    [InlineData("https://calendar.google.com/calendar/embed?src=x", "<iframe src=\"https://calendar.google.com/calendar/embed?src=x\"></iframe>")]
    [InlineData("https://www.google.com/maps/embed?pb=1&amp;hl=zh-TW", "<iframe src=\"https://www.google.com/maps/embed?pb=1&amp;hl=zh-TW\" width=\"600\"></iframe>")]
    [InlineData("https://player.vimeo.com/video/123", "<iframe src=\"https://player.vimeo.com/video/123\" allowfullscreen=\"\"></iframe>")]
    [InlineData("/forms/signup.php", "<iframe src=\"/forms/signup.php\"></iframe>")]
    public void OtherIframes_KeptInPlace_AsTheOriginalHtml(string src, string expected)
    {
        // 原本:迴圈只處理 YouTube 與 PDF,其他 iframe 原樣留在 DOM,而 ReverseMarkdown(Unknown = Bypass)
        // 只輸出子節點——iframe 沒有子節點,於是整個消失,沒有任何清單或 warning 記得它。
        // 遷移後的頁面靜默缺內容(地圖、行事曆、表單、非 YouTube 影片),README 卻說 embeds 會保留
        var attributes = src.Contains("maps") ? " width=\"600\"" : src.Contains("vimeo") ? " allowfullscreen" : "";
        var html = "<html><body><section id=\"personal\"><p>行事曆如下</p>"
            + $"<iframe src=\"{src}\"{attributes}></iframe><p>以上</p></section></body></html>";

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        Assert.Equal($"行事曆如下\n\n{expected}\n\n以上", result.BodyMarkdown);
        Assert.Empty(result.Videos);          // 不認得的嵌入不猜型別,frontmatter 不變
        Assert.Empty(result.Documents);
        Assert.Empty(result.NeedFetch);
    }

    [Theory]
    [InlineData("https://youtu.be/abc123")]
    [InlineData("https://www.YouTube.com/embed/abc123?start=30")]
    [InlineData("https://www.youtube-nocookie.com/embed/abc123")]   // 隱私強化模式:不含 youtube.com/embed 子字串
    public void YoutubeIframe_ShortLinkAndMixedCase_AreVideos(string src)
    {
        var html = $"<html><body><section id=\"personal\"><p>開場</p><iframe src=\"{src}\"></iframe></section></body></html>";

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        Assert.Equal(new Dictionary<string, string> { ["type"] = "youtube", ["url"] = src }, Assert.Single(result.Videos));
        Assert.Contains($"<iframe src=\"{src}\" title=\"video\"", result.BodyMarkdown);
    }

    [Theory]
    [InlineData(79, true)]    // 「少於」門檻才標
    [InlineData(80, false)]   // 剛好等於門檻(預設 80)不標
    public void TextInImage_ThresholdIsStrictlyLessThan(int textLength, bool flagged)
    {
        AddMediaFile("img/poster.jpg");
        var page = ArticlePage with { Section = "events", Slug = "x", TranslationKey = "events/x" };
        var html = $"<html><body><section id=\"event\"><p>{new string('字', textLength)}</p>"
            + "<img src=\"/img/poster.jpg\" alt=\"\"></section></body></html>";

        var result = _extractor.Extract(page, html, _mediaRoot);

        Assert.Equal(textLength, result.TextLength);
        Assert.Equal(flagged, result.Flags.Contains("text_in_image"));
    }

    [Theory]
    [InlineData("<video controls></video>")]                         // 沒有 source
    [InlineData("<video><source src=\"http://[broken\"></video>")]   // src 解析不了
    [InlineData("<video><source src=\"/\"></video>")]                // 指到站根,不是檔案
    public void VideoWithoutUsableSource_IsRemoved(string videoHtml)
    {
        var html = $"<html><body><section id=\"personal\"><p>內文</p>{videoHtml}</section></body></html>";

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        Assert.Empty(result.Videos);
        Assert.Empty(result.NeedFetch);
        // 逐字比對整段內文:「移除」包含不留下空的 16:9 外框(VideoWrap)。
        // 只斷言不含 "<video" 擋不住這種回歸——外框是 div,而 <video> 標籤 ReverseMarkdown 本來就會丟
        Assert.Equal("內文", result.BodyMarkdown);
    }

    [Fact]
    public void ScriptAndStyle_InsideContent_AreStripped()
    {
        // 正文 selector 常常整塊包到模板內嵌的 <script>/<style>;轉成 Markdown 後它們會變成
        // 一段看得見的程式碼文字(最壞的情況是可執行的內嵌 script 被搬進新站)。
        // 目前 PageExtractor 先剝、ReverseMarkdown 自己也會丟——這條守的是輸出結果,
        // 不論是哪一層剝掉的(換 Markdown 轉換器或升版時,少了任一層都還要是乾淨的)
        const string html =
            """
            <html><body><section id="intro">
            <p>正文</p>
            <script>window.tracker = 'abc';</script>
            <style>.banner { color: red; }</style>
            </section></body></html>
            """;
        var page = ArticlePage with { Section = "austin", TranslationKey = "austin/intro", Slug = "intro" };

        var result = _extractor.Extract(page, html, _mediaRoot);

        Assert.Contains("正文", result.BodyMarkdown);
        Assert.DoesNotContain("tracker", result.BodyMarkdown);
        Assert.DoesNotContain("banner", result.BodyMarkdown);
        Assert.DoesNotContain("<script", result.BodyMarkdown);
    }

    [Fact]
    public void UnresolvableImageSrc_IsIgnored_NotCountedMissing()
    {
        const string html =
            """
            <html><body><section id="intro"><p>這頁的文字夠長,不會被判成資訊鎖在圖裡的那一類頁面。</p>
            <img src="http://[broken" alt="壞網址"></section></body></html>
            """;
        var page = ArticlePage with { Section = "austin", TranslationKey = "austin/intro", Slug = "intro" };

        var result = _extractor.Extract(page, html, _mediaRoot);

        Assert.Empty(result.Images);
        Assert.Empty(result.MissingImages);
        Assert.Empty(result.MediaUses);
    }

    [Fact]
    public void ListingPage_ShortTextWithImage_IsNotFlaggedTextInImage()
    {
        // 列表頁本來就是一排縮圖配短標題,標 text_in_image 只會把每一個列表頁都列進待辦
        AddMediaFile("ch/news/images/thumb.jpg");
        var page = ArticlePage with { Slug = "index", TranslationKey = "news/index" };
        const string html =
            """
            <html><body><section id="list"><img src="images/thumb.jpg" alt=""></section></body></html>
            """;

        var result = _extractor.Extract(page, html, _mediaRoot);

        Assert.Equal("listing", result.PageType);
        Assert.DoesNotContain("text_in_image", result.Flags);
    }

    [Fact]
    public void NoTitleAnywhere_FallsBackToSlug()
    {
        var page = ArticlePage with { Section = "austin", TranslationKey = "austin/intro", Slug = "intro" };

        var result = _extractor.Extract(page, "<html><body><section id=\"x\">文字</section></body></html>", _mediaRoot);

        Assert.Equal("intro", result.Title);   // verify 要求 title 非空,不能留空字串
    }

    [Fact]
    public void PdfLabel_FallsBackToViewPdf_ForLocalesWithoutALabel()
    {
        var page = ArticlePage with { Locale = "ja", LangPrefix = "jp" };
        const string html =
            """
            <html><body><section id="personal"><iframe src="/files/a.pdf"></iframe></section></body></html>
            """;

        var result = _extractor.Extract(page, html, _mediaRoot);

        Assert.Contains("📄 View PDF", result.BodyMarkdown);
    }

    [Fact]
    public void Alternates_OnlyLinkRelAlternateTokens_NotAnchorHreflang()
    {
        // 認的是文件中任何位置、rel 含 alternate token 的 <link hreflang href>(作者宣告的語言版本),
        // 不限 <head>——現行實作對整份文件下 selector,body 裡的 <link> 也算(見 docs/公開介面盤點.md 3.5、7.5);
        // 若之後決定只看 head,另寫一條測試把那個決定釘住。這裡的測資沒有 body 裡的 <link>。
        // <a hreflang> 不算:它是「這個連結指向的東西是什麼語言」,內文一句「另見英文說明」不是本頁的英文版。
        const string html =
            """
            <html><head>
            <link rel="alternate" hreflang="en" href="/en/news/20260712.php">
            <link rel="ALTERNATE stylesheet" hreflang=" ja " href="../../jp/news/20260712.php">
            <link rel="canonical" hreflang="fr" href="/fr/x.php">
            <link hreflang="de" href="/de/x.php">
            <link rel="alternate" hreflang="  " href="/ko/x.php">
            <link rel="alternate" hreflang="es" href="http://[broken">
            </head><body><section id="personal">
            <p>另見 <a rel="alternate" hreflang="en" href="/en/other.php">英文說明</a></p>
            </section></body></html>
            """;

        var result = _extractor.Extract(ArticlePage, html, _mediaRoot);

        Assert.Equal(
        [
            ("en", "https://www.ibps-austin.org/en/news/20260712.php"),
            ("ja", "https://www.ibps-austin.org/jp/news/20260712.php"),   // rel 是 token 集、不分大小寫;值去空白
        ],
        result.Alternates.Select(a => (a.Hreflang, a.TargetUrl)));
        Assert.Equal("../../jp/news/20260712.php", result.Alternates[1].Href);   // 原值照記,不正規化
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
