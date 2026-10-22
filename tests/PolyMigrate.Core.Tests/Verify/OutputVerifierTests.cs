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
    /// 一般測試用,**孤島偵測是關掉的**(豁免已加入的每一頁)——名字直說,因為這裡
    /// 多數迷你站的頁本來就沒人連,那是測試素材的性質、不是被測行為。
    /// 要驗孤島偵測請用 <see cref="RunWithOrphanCheck"/>,別在這裡等它報 warning。
    /// </summary>
    private VerifyReport RunIgnoringUnlinked() =>
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

        var report = RunIgnoringUnlinked();

        Assert.Empty(report.Issues);
        Assert.Equal(3, report.PagesChecked);
        Assert.Equal(2, report.LinksChecked);
        Assert.Equal(1, report.MediaChecked);
    }

    [Fact]
    public void BrokenInternalLink_IsError()
    {
        AddPage("ch/news/a.md", body: "[gone](/ch/news/nope)");

        var report = RunIgnoringUnlinked();

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

        var report = RunIgnoringUnlinked();

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

        var report = RunIgnoringUnlinked();

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

        var report = RunIgnoringUnlinked();

        Assert.Contains(report.Issues, i => i.Kind == "missing_field" && i.Detail == "title");
        Assert.True(report.Errors >= 5);
    }

    [Fact]
    public void EncodedMediaPath_ResolvedAgainstDecodedDisk()
    {
        // §2.6:磁碟解碼名、URL 單次編碼——verify 要能對回去
        AddMedia("images/a b.jpg");
        AddPage("ch/a.md", body: "![](/media/images/a%20b.jpg)");

        var report = RunIgnoringUnlinked();

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void LanguageHomeAndRootLinks_Resolve()
    {
        AddPage("ch/index.md", body: "[root](/) [home](/ch/)");
        AddPage("index.md");

        var report = RunIgnoringUnlinked();

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void ExternalAndAnchorLinks_Ignored()
    {
        AddPage("ch/a.md", body: "[x](https://other.org/) [y](#top) [z](mailto:a@b.c) [p](//cdn/x)");

        var report = RunIgnoringUnlinked();

        Assert.Empty(report.Issues);
        Assert.Equal(0, report.LinksChecked);
    }

    [Fact]
    public void HtmlLinks_SingleQuotedAndUppercase_AreChecked()
    {
        // 內嵌 HTML 的 href/src 單引號、大寫屬性也要抽出來驗,否則壞連結漏報
        AddPage("ch/a.md", body: "<a href='/ch/news/nope'>x</a> <IMG SRC=\"/ch/news/gone\">");

        var report = RunIgnoringUnlinked();

        Assert.Equal(2, report.Errors);
        Assert.Contains(report.Issues, i => i.Kind == "broken_link" && i.Detail == "/ch/news/nope");
        Assert.Contains(report.Issues, i => i.Kind == "broken_link" && i.Detail == "/ch/news/gone");
    }

    [Theory]
    [InlineData("data-src")]        // lazy-load 的 iframe / img
    [InlineData("data-href")]
    [InlineData("xsrc")]
    public void AttributesThatOnlyEndInSrcOrHref_AreNotLinks(string attribute)
    {
        // href=/src= 的比對原本前面沒有邊界:data-src、data-href 也被當成站內連結檢查。
        // 它們不是瀏覽器會載入的網址(要靠站上的 JS),報 broken_link 是假警報
        AddPage("ch/a.md", body: $"<iframe {attribute}=\"/ch/news/nope\" src=\"/ch/a\"></iframe>");

        var report = RunIgnoringUnlinked();

        Assert.Equal(0, report.Errors);
        Assert.Equal(1, report.LinksChecked);   // 只剩真正的 src
    }

    [Fact]
    public void MediaRef_WithQueryString_ResolvesToFile()
    {
        // /media/x.jpg?v=2 的 ?v=2 是快取破壞參數,不是檔名一部分——去掉再對磁碟找
        AddMedia("images/a.jpg");
        AddPage("ch/a.md", body: "![](/media/images/a.jpg?v=2)");

        var report = RunIgnoringUnlinked();

        Assert.Empty(report.Issues);
        Assert.Equal(1, report.MediaChecked);
    }

    [Theory]
    [InlineData("/media/files/a.pdf#page=3")]       // PDF 直接跳頁
    [InlineData("/media/files/a.pdf?dl=1#page=3")]
    public void MediaRef_FragmentIsNotPartOfTheFileName(string reference)
    {
        AddMedia("files/a.pdf");
        AddPage("ch/a.md", body: $"[講義]({reference})");

        var report = RunIgnoringUnlinked();

        Assert.Empty(report.Issues);
        Assert.Equal(1, report.MediaChecked);
    }

    [Fact]
    public void KnownMissingImage_ReferencedWithAQueryString_IsStillAWarning()
    {
        // missing_images.csv 記的是不帶 query 的網路路徑;內文多了 ?v=2 的同一張原站壞圖仍是「已知」,
        // 不可因為快取參數就從 warning 升成 error(exit 1 → 2)。回報的 detail 保留原寫法
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        PolyMigrate.Core.Inventory.Csv.Write(Path.Combine(_root, "missing_images.csv"),
        [
            new[] { "source_page", "missing_image" },
            new[] { "https://example.org/ch/a.php", "/media/known.jpg" },
        ]);
        AddPage("ch/a.md", body: "![](/media/known.jpg?v=2)");

        var report = RunIgnoringUnlinked();

        var issue = Assert.Single(report.Issues);
        Assert.Equal((Severity.Warning, "known_missing_media", "/media/known.jpg?v=2"),
            (issue.Severity, issue.Kind, issue.Detail));
    }

    [Fact]
    public void CrlfLineEndings_VerifyTheSameAsLf()
    {
        // 輸出被 git(autocrlf)或 Windows 編輯器轉成 CRLF 很常見,而 Windows 上 extract 寫的
        // frontmatter 本身就是 CRLF(contracts.md「通用規則」)。圍欄與欄位比對若不先正規化行尾,
        // 這些頁會全部變成 invalid_frontmatter
        AddMedia("ok.jpg");
        var path = Path.Combine(_root, "content", "ch", "news", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "---\r\nsource_url: https://example.org/ch/news/a.php\r\nlang: zh-Hant\r\nslug: a\r\n"
            + "translation_key: news/a\r\ntitle: 標題\r\npage_type: article\r\n"
            + "images:\r\n- local: /media/ok.jpg\r\n  alt: ''\r\n---\r\n\r\n[b](/ch/news/b)\r\n");
        _pages.Add("ch/news/a.md");
        AddPage("ch/news/b.md");

        var report = RunIgnoringUnlinked();

        Assert.Empty(report.Issues);
        Assert.Equal(1, report.LinksChecked);
        Assert.Equal(1, report.MediaChecked);   // frontmatter images[].local 一樣讀得到
    }

    [Fact]
    public void NoContentDirectory_LeavesTheGivenDirectoryUntouched()
    {
        // 最常見的指錯是把 raw/(不可再生的鏡像)當成輸出根目錄:回報 no_content,
        // 但不在那裡留下 verify_report.csv 或任何東西
        File.WriteAllText(Path.Combine(_root, "index.php.html"), "<html></html>");

        var report = new OutputVerifier().Run(_root, mediaDir: null);

        Assert.Equal("no_content", Assert.Single(report.Issues).Kind);
        Assert.Equal(["index.php.html"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void MalformedImageLocal_NonString_DoesNotCrash()
    {
        // 手改壞的 frontmatter 讓 images[].local 是數字而非字串 → 略過該筆,不得崩潰
        AddPage("ch/a.md", frontmatterExtra: "images:\n- local: 12345\n  alt: ''\n");

        var report = RunIgnoringUnlinked();   // 不應丟 InvalidCastException

        Assert.DoesNotContain(report.Issues, i => i.Kind is "missing_media" or "known_missing_media");
    }

    [Fact]
    public void ReportCsv_Written()
    {
        AddPage("ch/a.md", body: "[gone](/nope)");

        RunIgnoringUnlinked();

        var lines = File.ReadAllLines(Path.Combine(_root, "verify_report.csv"));
        Assert.Contains("severity,page,kind,detail", lines[0]);
        Assert.Contains(lines, l => l.Contains("broken_link"));
    }

    [Fact]
    public void ReportCsv_SortedBySeverityThenPageThenDetail_WithWireSeverityStrings()
    {
        // 決定性輸出(§3.10):同一份輸出重跑必須逐位元組相同,排序規則就是契約的一部分。
        // 素材刻意讓每一個排序鍵都有作用:warning 那頁(ch/a)字典序最前、
        // ch/b 的兩個 detail 依發現順序是 /zzz 在前
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        File.WriteAllText(Path.Combine(_root, "missing_images.csv"),
            "﻿source_page,missing_image\r\nhttps://example.org/ch/a.php,/media/known.jpg\r\n");
        AddPage("ch/a.md", body: "![](/media/known.jpg)");
        AddPage("ch/b.md", body: "[x](/zzz) [y](/aaa)");
        AddPage("ch/c.md", body: "[z](/mmm)");

        RunIgnoringUnlinked();

        var lines = File.ReadAllText(Path.Combine(_root, "verify_report.csv")).Split("\r\n");
        Assert.Equal(
        [
            "severity,page,kind,detail",
            "error,ch/b.md,broken_link,/aaa",
            "error,ch/b.md,broken_link,/zzz",
            "error,ch/c.md,broken_link,/mmm",
            "warning,ch/a.md,known_missing_media,/media/known.jpg",
            "",
        ], lines);
    }

    [Fact]
    public void NoContentDirectory_IsASingleError()
    {
        // 指錯目錄(例如 verify raw/ 而不是輸出根目錄)必須是 error,不是「0 頁、全部通過」
        var report = new OutputVerifier().Run(_root, Path.Combine(_root, "media"));

        var issue = Assert.Single(report.Issues);
        Assert.Equal(Severity.Error, issue.Severity);
        Assert.Equal("no_content", issue.Kind);
        Assert.Equal("", issue.Page);
        Assert.Equal($"content directory not found: {Path.Combine(_root, "content")}", issue.Detail);
        Assert.Equal(1, report.Errors);
        Assert.Equal(0, report.PagesChecked);
    }

    [Theory]
    [InlineData("沒有圍欄,直接是內文\n")]
    [InlineData("---\ntitle: 只有開頭圍欄\n\n內文\n")]
    [InlineData("\n---\ntitle: 圍欄前有空行\n---\n\n內文\n")]
    public void MissingFrontmatterFence_IsInvalidFrontmatter(string content)
    {
        var path = Path.Combine(_root, "content", "ch", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var report = RunIgnoringUnlinked();

        // 只報一筆、不再追加 missing_field:沒有 frontmatter 就沒有欄位可查
        var issue = Assert.Single(report.Issues);
        Assert.Equal((Severity.Error, "ch/a.md", "invalid_frontmatter", "no frontmatter block"),
            (issue.Severity, issue.Page, issue.Kind, issue.Detail));
    }

    [Fact]
    public void BrokenYamlInFrontmatter_IsInvalidFrontmatter_NotACrash()
    {
        // 手改壞的 frontmatter 不得讓整個巡檢崩潰,其餘頁照樣檢查
        var path = Path.Combine(_root, "content", "ch", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\ntitle: [沒有收尾\nslug: a\n---\n\n內文\n");
        AddPage("ch/b.md", body: "[gone](/nope)");

        var report = RunIgnoringUnlinked();

        Assert.Contains(report.Issues, i => i is { Page: "ch/a.md", Kind: "invalid_frontmatter", Severity: Severity.Error });
        Assert.DoesNotContain(report.Issues, i => i.Page == "ch/a.md" && i.Kind == "missing_field");
        Assert.Contains(report.Issues, i => i is { Page: "ch/b.md", Kind: "broken_link" });
        Assert.Equal(2, report.PagesChecked);
    }

    [Theory]
    [InlineData("title:\n")]          // 空值(YAML null)
    [InlineData("title: ''\n")]       // 空字串
    [InlineData("title: [a, b]\n")]   // 不是字串
    public void RequiredFieldPresentButNotANonEmptyString_IsMissingField(string titleLine)
    {
        var path = Path.Combine(_root, "content", "ch", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "---\nsource_url: https://example.org/ch/a.php\nlang: zh-Hant\nslug: a\n"
            + "translation_key: a\n" + titleLine + "page_type: page\n---\n\n內文\n");
        _pages.Add("ch/a.md");

        var report = RunIgnoringUnlinked();

        var issue = Assert.Single(report.Issues);
        Assert.Equal(("missing_field", "title"), (issue.Kind, issue.Detail));
    }

    [Fact]
    public void EmptyFrontmatterBlock_EveryRequiredFieldMissing()
    {
        // 圍欄在、內容空(YAML 解出 null):每個必填欄位都要報,而不是當成「沒問題」或崩潰
        var path = Path.Combine(_root, "content", "ch", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\n\n---\n\n內文\n");
        _pages.Add("ch/a.md");

        var report = RunIgnoringUnlinked();

        Assert.Equal(
            ["source_url", "lang", "slug", "translation_key", "title", "page_type"],
            report.Issues.Where(i => i.Kind == "missing_field").Select(i => i.Detail));
        Assert.Equal(6, report.Errors);
    }

    [Fact]
    public void MediaDirectoryAbsent_MediaChecksSkipped_NotReportedAsMissing()
    {
        // 只拿到 content/ 沒拿到 media/(媒體另外部署)時,不可把每一張圖都報成 missing_media
        AddPage("ch/a.md", body: "![](/media/x.jpg) [b](/ch/b)");
        AddPage("ch/b.md");

        var absent = new OutputVerifier().Run(_root, Path.Combine(_root, "nope"), "/media/", _pages);
        var none = new OutputVerifier().Run(_root, mediaDir: null, "/media/", _pages);

        foreach (var report in (VerifyReport[])[absent, none])
        {
            Assert.True(report.MediaChecksSkipped);
            Assert.Equal(0, report.MediaChecked);
            Assert.Empty(report.Issues);
            Assert.Equal(1, report.LinksChecked);   // 連結檢查照做
        }
    }

    [Fact]
    public void MediaDirectoryPresent_ChecksAreNotSkipped()
    {
        AddMedia("x.jpg");
        AddPage("ch/a.md", body: "![](/media/x.jpg)");

        var report = RunIgnoringUnlinked();

        Assert.False(report.MediaChecksSkipped);
        Assert.Equal(1, report.MediaChecked);
    }

    [Fact]
    public void CustomMediaPrefix_DecidesWhatCountsAsMedia()
    {
        // --media-prefix 決定哪些引用拿去對磁碟:/assets/ 底下的才是媒體,
        // 其餘 "/" 開頭的(包括 /media/...)一律當站內連結驗
        AddMedia("ok.jpg");
        AddPage("ch/a.md", body: "![](/assets/ok.jpg) ![](/assets/gone.jpg) ![](/media/ok.jpg)");

        var report = new OutputVerifier().Run(_root, Path.Combine(_root, "media"), "/assets/", _pages);

        Assert.Equal(2, report.MediaChecked);
        Assert.Equal(1, report.LinksChecked);
        Assert.Equal(2, report.Errors);
        Assert.Contains(report.Issues, i => i is { Kind: "missing_media", Detail: "/assets/gone.jpg" });
        Assert.Contains(report.Issues, i => i is { Kind: "broken_link", Detail: "/media/ok.jpg" });
    }

    [Fact]
    public void KnownMissingCsv_WithoutTrailingNewline_StillRecognized()
    {
        // 人工在 Excel 編過再存回的 missing_images.csv 常常沒有結尾換行;最後一列不能因此被吃掉,
        // 否則一張已知的原站壞圖會從 warning 升成 error(exit 1 → 2)
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        File.WriteAllText(Path.Combine(_root, "missing_images.csv"),
            "source_page,missing_image\r\nhttps://example.org/ch/a.php,/media/a.jpg\r\nhttps://example.org/ch/a.php,/media/b.jpg");
        AddPage("ch/a.md", body: "![](/media/a.jpg) ![](/media/b.jpg)");

        var report = RunIgnoringUnlinked();

        Assert.Equal(0, report.Errors);
        Assert.Equal(2, report.Warnings);
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

    [Fact]
    public void PageOnlyEmbeddedByAnIframe_CountsAsLinked()
    {
        // 現況(待維護者裁定,見決策紀錄「被 iframe 嵌入算不算有入口」):iframe 的 src 與 <a href> 一樣
        // 算進入口。修 H3 之前保留的 iframe 根本不在 Markdown 裡,這種頁原本報 unlinked_page(exit 1),
        // 現在不報(exit 0)。改判的話這條要一起改
        AddPage("ch/index.md", body: "<iframe src=\"/ch/forms/signup\"></iframe>", pageType: "page");
        AddPage("ch/forms/signup.md");

        var report = RunWithOrphanCheck();

        Assert.Empty(report.Issues);
    }

    [Theory]
    // extract 改寫內文連結時刻意保留 query 與 fragment(LinkRewriterTests.QueryAndFragment_Preserved),
    // 所以 verify 一定會看到這種連結:它們連到的是那一頁,不是壞連結,那一頁也不是孤島
    [InlineData("/ch/news/b#comments")]
    [InlineData("/ch/news/b?page=2")]
    [InlineData("/ch/news/b/?page=2#top")]
    public void InternalLink_WithQueryOrFragment_ResolvesToThePage(string href)
    {
        AddPage("ch/index.md", body: $"[b]({href})", pageType: "page");
        AddPage("ch/news/b.md");

        var report = RunWithOrphanCheck();

        Assert.Empty(report.Issues);
        Assert.Equal(1, report.LinksChecked);
    }

    [Fact]
    public void PercentEncodedCjkLink_ResolvesToTheDecodedPage()
    {
        // 瀏覽器與編輯器產出中日韓 href 預設是編碼的,而鏡像檔名是解碼的(§2.6)。
        // 直接比字串會把「存在、而且真的被連到」的頁報成壞連結,再連帶誤報成孤島——
        // 對 i18n-first 的工具來說那是旗艦情境,不是邊角。
        AddPage("ch/index.md", body: "[禪修](/ch/news/%E7%A6%AA%E4%BF%AE)", pageType: "page");
        AddPage("ch/news/禪修.md");

        var report = RunWithOrphanCheck();

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void PercentEncodedLinkToNothing_IsStillBroken()
    {
        // 解碼只是多試一次,不是放行:解完還是對不上就照樣報壞連結,且回報原始寫法
        AddPage("ch/index.md", body: "[無](/ch/news/%E4%B8%8D%E5%AD%98%E5%9C%A8)", pageType: "page");

        var report = RunWithOrphanCheck();

        var issue = Assert.Single(report.Issues);
        Assert.Equal("broken_link", issue.Kind);
        Assert.Equal("/ch/news/%E4%B8%8D%E5%AD%98%E5%9C%A8", issue.Detail);
    }

    [Fact]
    public void SelfLink_DoesNotCountAsAnEntrance()
    {
        // 訪客得先有辦法到這一頁,才看得到頁上那個指向自己的連結——
        // 否則一個 canonical / 麵包屑的自我連結就會讓這頁靜靜逃掉偵測
        AddPage("ch/news/b.md", body: "[我自己](/ch/news/b)");

        var report = RunWithOrphanCheck();

        var issue = Assert.Single(report.Issues);
        Assert.Equal("unlinked_page", issue.Kind);
        Assert.Equal("ch/news/b.md", issue.Page);
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
