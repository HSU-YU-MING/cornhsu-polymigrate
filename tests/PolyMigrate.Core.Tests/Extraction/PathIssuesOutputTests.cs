using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>
/// path_issues.csv 的內容列(contracts.md §3.4)。既有測試只驗過「乾淨站只有表頭」,
/// 而真正有人會去讀的是有問題時的那幾列:哪一頁、哪一級、為什麼。
/// </summary>
public class PathIssuesOutputTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-pathissues").FullName;
    private string RawDir => Path.Combine(_root, "raw");
    private string OutDir => Path.Combine(_root, "out");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SiteConfig Config() => new()
    {
        Site = new SiteSection { BaseUrl = "https://example.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant" },
            DefaultLang = "zh-Hant",
            StripExtensions = [".php", ".asp"],
        },
        Extract = new ExtractSection { Content = "main" },
    };

    private void AddRaw(string relative)
    {
        var path = Path.Combine(RawDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html><head><title>t</title></head><body><main>text</main></body></html>");
    }

    [Fact]
    public void ErrorAndWarningRows_WrittenWithSeverityFirstOrdering()
    {
        // error:a.asp 與 a.php 都收斂成 content/ch/zz/a.md,後處理的那頁被拒寫
        AddRaw("ch/zz/a.asp.html");
        AddRaw("ch/zz/a.php.html");
        // warning:輸出絕對路徑超過傳統 Windows 260 上限(單段 < 255,三平台檔案系統都建得出來)
        var longSlug = new string('x', 230);
        AddRaw($"ch/aa/{longSlug}.php.html");

        var report = new ExtractionPipeline(Config()).Run(
            new ExtractionPaths(RawDir, Path.Combine(_root, "media"), OutDir));

        Assert.True(report.HasErrors);
        Assert.True(report.HasWarnings);   // 超長路徑是 warning 的來源之一(無缺圖、無待補媒體)
        Assert.Equal(1, report.PagesSkippedUnsafe);
        Assert.Equal(2, report.PathIssues.Count);

        var lines = File.ReadAllText(Path.Combine(OutDir, "path_issues.csv")).Split("\r\n");
        Assert.Equal("severity,page,issue", lines[0]);
        // 排序是 severity 字串優先:warning 那頁的路徑(content/ch/aa/...)字典序較前,
        // 若只依 page 排,它會排在 error 前面
        Assert.Equal(
            "error,content/ch/zz/a.md,duplicate output path (another source also maps to 'content/ch/zz/a.md')",
            lines[1]);
        Assert.StartsWith($"warning,content/ch/aa/{longSlug}.md,path length ", lines[2]);
        Assert.EndsWith(" exceeds classic Windows limit (260)", lines[2]);
        Assert.Equal("", lines[3]);   // 結尾 CRLF 之後沒有別的東西
        Assert.Equal(4, lines.Length);

        // 超長只是 warning:照寫(也照進 redirect)。被拒的那頁則不寫
        Assert.True(File.Exists(Path.Combine(OutDir, "content", "ch", "aa", longSlug + ".md")));
        Assert.Contains($"/ch/aa/{longSlug}", File.ReadAllText(Path.Combine(OutDir, "redirect_map.csv")));
        Assert.Equal(2, report.PagesWritten);
    }

    [Fact]
    public void SkippedPage_ContributesToNoInventory()
    {
        // CLAUDE.md「已知教訓」:被拒寫的頁原本仍被聚合進清單——redirect 打到不存在的頁(→404)。
        // 同一個理由適用於每一份清單:它的圖、待補 PDF、壞圖都不該出現(磁碟上沒有那一頁)
        AddRaw("ch/zz/a.asp.html");
        var skipped = Path.Combine(RawDir, "ch", "zz", "a.php.html");
        File.WriteAllText(skipped,
            "<html><body><main><img src=\"/img/only-in-skipped.jpg\"><img src=\"/img/gone.jpg\">"
            + "<iframe src=\"/files/only-in-skipped.pdf\"></iframe></main></body></html>");
        Directory.CreateDirectory(Path.Combine(_root, "media", "img"));
        File.WriteAllText(Path.Combine(_root, "media", "img", "only-in-skipped.jpg"), "jpeg");

        var report = new ExtractionPipeline(Config()).Run(
            new ExtractionPaths(RawDir, Path.Combine(_root, "media"), OutDir));

        Assert.Equal(1, report.PagesSkippedUnsafe);
        Assert.Equal(0, report.MediaReferenced);
        Assert.Equal(0, report.MissingImages);
        Assert.Equal(0, report.NeedFetchMedia);
        Assert.DoesNotContain("only-in-skipped", File.ReadAllText(Path.Combine(OutDir, "media_manifest.csv")));
        Assert.DoesNotContain("gone.jpg", File.ReadAllText(Path.Combine(OutDir, "missing_images.csv")));
        Assert.Equal("", File.ReadAllText(Path.Combine(OutDir, "need_fetch_media.txt")));
        Assert.DoesNotContain("a.php", File.ReadAllText(Path.Combine(OutDir, "redirect_map.csv")));
    }
}
