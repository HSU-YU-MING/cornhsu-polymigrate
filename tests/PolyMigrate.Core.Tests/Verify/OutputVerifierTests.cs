using PolyMigrate.Core.Diagnostics;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core.Tests.Verify;

public class OutputVerifierTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-verify").FullName;

    private readonly List<string> _pages = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void AddPage(string relative, string frontmatterExtra = "", string body = "",
        string pageType = "article")
    {
        var path = Path.Combine(_root, "content", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var slug = Path.GetFileNameWithoutExtension(relative);
        File.WriteAllText(path,
            $"""
            ---
            source_url: https://example.org/{relative}
            lang: zh-Hant
            section: news
            slug: {slug}
            translation_key: news/{slug}
            title: 標題
            page_type: {pageType}
            {frontmatterExtra}---

            {body}
            """.Replace("\r", ""));
        _pages.Add(relative);
    }

    private void AddMedia(string relative)
    {
        var path = Path.Combine(_root, "media", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    /// <summary>
    /// 一般測試用。孤島偵測會對「沒有任何頁連到」的頁報 warning,而這裡多數迷你站的頁
    /// 本來就沒人連——那是測試素材的性質,不是被測行為。故一律豁免已加入的頁,
    /// 讓每個測試只驗自己那一件事;孤島偵測本身改用 <see cref="RunWithOrphanCheck"/>。
    /// </summary>
    private VerifyReport Run() =>
        new OutputVerifier().Run(_root, Path.Combine(_root, "media"), "/media/", _pages);

    /// <summary>孤島偵測的測試用:不給豁免(或只給指定的幾筆)。</summary>
    private VerifyReport RunWithOrphanCheck(params string[] allowUnlinked) =>
        new OutputVerifier().Run(_root, Path.Combine(_root, "media"), "/media/", allowUnlinked);

    [Fact]
    public void CleanSite_NoIssues()
    {
        AddMedia("ch/news/images/a.jpg");
        AddPage("ch/news/a.md",
            body: "看 [另一篇](/ch/news/b) 和 [列表](/ch/news/) 和 ![](/media/ch/news/images/a.jpg)");
        AddPage("ch/news/b.md");
        AddPage("ch/news/index.md");

        var report = Run();

        Assert.Empty(report.Issues);
        Assert.Equal(3, report.PagesChecked);
        Assert.Equal(2, report.LinksChecked);
        Assert.Equal(1, report.MediaChecked);
    }

    [Fact]
    public void BrokenInternalLink_IsError()
    {
        AddPage("ch/news/a.md", body: "[gone](/ch/news/nope)");

        var report = Run();

        var issue = Assert.Single(report.Issues);
        Assert.Equal((Severity.Error, "broken_link", "/ch/news/nope"), (issue.Severity, issue.Kind, issue.Detail));
    }

    [Fact]
    public void MissingMedia_IsError_ButKnownMissingIsWarning()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        AddPage("ch/news/a.md", body: "![](/media/x.jpg) ![](/media/known.jpg)");
        PolyMigrate.Core.Inventory.Csv.Write(Path.Combine(_root, "missing_images.csv"),
        [
            new[] { "source_page", "missing_image" },
            new[] { "https://example.org/ch/news/a.php", "/media/known.jpg" },
        ]);

        var report = Run();

        Assert.Equal(1, report.Errors);
        Assert.Equal(1, report.Warnings);
        Assert.Contains(report.Issues, i => i.Kind == "missing_media" && i.Detail == "/media/x.jpg");
        Assert.Contains(report.Issues, i => i.Kind == "known_missing_media" && i.Detail == "/media/known.jpg");
    }

    [Fact]
    public void FrontmatterGalleryImages_AreChecked()
    {
        AddMedia("ok.jpg");
        AddPage("ch/news/a.md", frontmatterExtra:
            """
            images:
            - local: /media/ok.jpg
              alt: ''
            - local: /media/gone.jpg
              alt: ''
            """ + "\n");

        var report = Run();

        var issue = Assert.Single(report.Issues);
        Assert.Equal("missing_media", issue.Kind);
        Assert.Equal("/media/gone.jpg", issue.Detail);
    }

    [Fact]
    public void MissingRequiredField_IsError()
    {
        var path = Path.Combine(_root, "content", "ch", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\nsource_url: https://x\n---\n\nbody\n");

        var report = Run();

        Assert.Contains(report.Issues, i => i.Kind == "missing_field" && i.Detail == "title");
        Assert.True(report.Errors >= 5);
    }

    [Fact]
    public void EncodedMediaPath_ResolvedAgainstDecodedDisk()
    {
        // §2.6:磁碟解碼名、URL 單次編碼——verify 要能對回去
        AddMedia("images/a b.jpg");
        AddPage("ch/a.md", body: "![](/media/images/a%20b.jpg)");

        var report = Run();

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void LanguageHomeAndRootLinks_Resolve()
    {
        AddPage("ch/index.md", body: "[root](/) [home](/ch/)");
        AddPage("index.md");

        var report = Run();

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ExternalAndAnchorLinks_Ignored()
    {
        AddPage("ch/a.md", body: "[x](https://other.org/) [y](#top) [z](mailto:a@b.c) [p](//cdn/x)");

        var report = Run();

        Assert.Empty(report.Issues);
        Assert.Equal(0, report.LinksChecked);
    }

    [Fact]
    public void HtmlLinks_SingleQuotedAndUppercase_AreChecked()
    {
        // 內嵌 HTML 的 href/src 單引號、大寫屬性也要抽出來驗,否則壞連結漏報
        AddPage("ch/a.md", body: "<a href='/ch/news/nope'>x</a> <IMG SRC=\"/ch/news/gone\">");

        var report = Run();

        Assert.Equal(2, report.Errors);
        Assert.Contains(report.Issues, i => i.Kind == "broken_link" && i.Detail == "/ch/news/nope");
        Assert.Contains(report.Issues, i => i.Kind == "broken_link" && i.Detail == "/ch/news/gone");
    }

    [Fact]
    public void MediaRef_WithQueryString_ResolvesToFile()
    {
        // /media/x.jpg?v=2 的 ?v=2 是快取破壞參數,不是檔名一部分——去掉再對磁碟找
        AddMedia("images/a.jpg");
        AddPage("ch/a.md", body: "![](/media/images/a.jpg?v=2)");

        var report = Run();

        Assert.Empty(report.Issues);
        Assert.Equal(1, report.MediaChecked);
    }

    [Fact]
    public void MalformedImageLocal_NonString_DoesNotCrash()
    {
        // 手改壞的 frontmatter 讓 images[].local 是數字而非字串 → 略過該筆,不得崩潰
        AddPage("ch/a.md", frontmatterExtra: "images:\n- local: 12345\n  alt: ''\n");

        var report = Run();   // 不應丟 InvalidCastException

        Assert.DoesNotContain(report.Issues, i => i.Kind is "missing_media" or "known_missing_media");
    }

    [Fact]
    public void ReportCsv_Written()
    {
        AddPage("ch/a.md", body: "[gone](/nope)");

        Run();

        var lines = File.ReadAllLines(Path.Combine(_root, "verify_report.csv"));
        Assert.Contains("severity,page,kind,detail", lines[0]);
        Assert.Contains(lines, l => l.Contains("broken_link"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 孤島頁面偵測:輸出正確、卻沒有任何頁連得到的頁。
    //
    // 這是 verify 原本的系統性盲區——沿著連結走的巡檢走不到它,於是回報「全部正常」。
    // 誤報的代價比漏報高(沒人看的警告會稀釋整份報告),所以豁免規則一併在這裡釘死。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnlinkedPage_IsWarning()
    {
        AddPage("ch/news/index.md", body: "[看這篇](/ch/news/a)", pageType: "listing");
        AddPage("ch/news/a.md");
        AddPage("ch/news/b.md");        // 沒有任何頁連到 b

        var report = RunWithOrphanCheck();

        var issue = Assert.Single(report.Issues);
        Assert.Equal(Severity.Warning, issue.Severity);
        Assert.Equal("unlinked_page", issue.Kind);
        Assert.Equal("ch/news/b.md", issue.Page);
    }

    [Fact]
    public void TopLevelSinglePage_IsWarning()
    {
        // 香雲寺 BLIA:頂層目錄索引、內容完全正確、選單漏了它 → 沒有任何入口。
        // 與列表頁的差別只在 page_type(所屬 section 未宣告型別 → page 而非 listing)。
        AddPage("index.md", pageType: "page");
        AddPage("en/index.md", pageType: "page");
        AddPage("en/blia/index.md", pageType: "page");

        var report = RunWithOrphanCheck();

        var issue = Assert.Single(report.Issues);
        Assert.Equal("unlinked_page", issue.Kind);
        Assert.Equal("en/blia/index.md", issue.Page);
    }

    [Fact]
    public void SiteRootLanguageHomeAndListing_AreExemptByDefault()
    {
        // 這三類本來就不會被內容頁連到:連回站根、連回語言首頁的頁幾乎不存在,
        // 列表頁則是從選單進入——而選單不在 Phase 2 輸出裡,verify 看不到。
        AddPage("index.md", pageType: "page");                   // 站根,深度 0
        AddPage("ch/index.md", pageType: "page");                // 語言首頁,深度 1
        AddPage("ch/news/index.md", body: "[a](/ch/news/a)", pageType: "listing");
        AddPage("ch/news/a.md");

        var report = RunWithOrphanCheck();

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void LinkedViaHtmlOrTrailingSlash_IsNotOrphan()
    {
        // 判定用的是正規化後的路由,所以尾斜線與內嵌 HTML 的 href 一樣算「連到了」
        AddPage("ch/index.md", body: "<a href='/ch/news/a/'>x</a>", pageType: "page");
        AddPage("ch/news/a.md");

        var report = RunWithOrphanCheck();

        Assert.Empty(report.Issues);
    }

    [Theory]
    [InlineData("ch/news/b.md")]    // 用 content 相對路徑豁免
    [InlineData("/ch/news/b")]      // 用路由豁免
    public void AllowUnlinked_SuppressesWarning(string exemption)
    {
        AddPage("ch/news/b.md");

        var report = RunWithOrphanCheck(exemption);

        Assert.Empty(report.Issues);
    }
}
