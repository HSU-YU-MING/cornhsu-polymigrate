using PolyMigrate.Core.Diagnostics;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core.Tests.Verify;

public class OutputVerifierTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-verify").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void AddPage(string relative, string frontmatterExtra = "", string body = "")
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
            page_type: article
            {frontmatterExtra}---

            {body}
            """.Replace("\r", ""));
    }

    private void AddMedia(string relative)
    {
        var path = Path.Combine(_root, "media", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private VerifyReport Run() => new OutputVerifier().Run(_root, Path.Combine(_root, "media"));

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
}
