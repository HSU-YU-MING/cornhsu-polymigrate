using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core.Tests;

/// <summary>
/// Golden-file 測試(§3.5):fixture 站跑完整管線,輸出與 committed 基準逐檔比對。
/// 基準更新:設環境變數 POLYMIGRATE_UPDATE_GOLDEN=1 跑一次測試,再人工 review diff。
/// </summary>
public class GoldenTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string FixtureDir = Path.Combine(RepoRoot, "tests", "fixtures", "site");
    private static readonly string GoldenDir = Path.Combine(RepoRoot, "tests", "fixtures", "golden");

    private readonly string _out = Directory.CreateTempSubdirectory("polymigrate-golden").FullName;

    public void Dispose() => Directory.Delete(_out, recursive: true);

    private ExtractionReport RunPipeline()
    {
        var config = SiteConfigLoader.LoadFile(Path.Combine(FixtureDir, "config.yaml"));
        return new ExtractionPipeline(config).Run(new ExtractionPaths(
            Path.Combine(FixtureDir, "raw"),
            Path.Combine(FixtureDir, "media"),
            _out));
    }

    [Fact]
    public void FixtureSite_MatchesGolden()
    {
        RunPipeline();

        if (Environment.GetEnvironmentVariable("POLYMIGRATE_UPDATE_GOLDEN") == "1")
        {
            UpdateGolden();
            return;
        }

        var goldenFiles = RelativeFiles(GoldenDir);
        var outputFiles = RelativeFiles(_out);
        Assert.Equal(goldenFiles, outputFiles);

        foreach (var rel in goldenFiles)
        {
            var expected = Normalize(rel, File.ReadAllText(Path.Combine(GoldenDir, rel)));
            var actual = Normalize(rel, File.ReadAllText(Path.Combine(_out, rel)));
            Assert.True(expected == actual, $"golden mismatch: {rel}\n--- expected ---\n{expected}\n--- actual ---\n{actual}");
        }
    }

    [Fact]
    public void FixtureSite_ReportMatchesSpec()
    {
        var report = RunPipeline();

        Assert.Equal(13, report.PagesWritten);
        Assert.Equal(9, report.TranslationKeys);
        Assert.Equal(2, report.SuggestedPairs);        // date 組 + events 組(hreflang)
        Assert.Equal(2, report.MissingImages);         // 20260101_3.jpg + broken.jpg
        Assert.Equal(1, report.NeedFetchMedia);        // schedule.pdf
        Assert.Empty(report.StaleOutputs);             // 乾淨的一次跑完,content/ 裡不該有多餘的檔

        // hreflang:6 則宣告裡只有 2 則可用——其餘是自我指涉、x-default、以及一則指到
        // 鏡像裡沒有的頁。這個比例是 fixture 刻意做出來的,舊站的 hreflang 就長這樣。
        Assert.Equal(6, report.HreflangDeclared);
        Assert.Equal(2, report.HreflangUsable);
    }

    [Fact]
    public void HreflangMap_RecordsUnusableDeclarations_NotJustTheGoodOnes()
    {
        // 這份檔案的用途是回答「這個站的 hreflang 能不能信」,所以壞掉的那些才是重點。
        // 只留可用的,等於把品質問題藏起來。
        RunPipeline();
        var rows = File.ReadAllLines(Path.Combine(_out, "hreflang_map.csv"));

        var broken = rows.Single(r => r.Contains("/en/news/20260214.php"));
        Assert.Contains("False,False,False", broken);            // in_mirror / reciprocal / usable

        var good = rows.Single(r =>
            r.StartsWith("https://legacy.example.org/ch/events/2026_chanxiu.php,en,"));
        Assert.Contains("events/enRetreat,True,True,True", good);

        // x-default 指到語言選擇頁:在鏡像裡,但配對上零資訊
        var xDefault = rows.Single(r => r.Contains(",x-default,"));
        Assert.Contains("/index,True,False,False", xDefault);
    }

    [Fact]
    public void SecondRun_AfterAPageLeavesTheMirror_ReportsItAsStale()
    {
        // 舊站下架一篇文章後重跑:extract 只寫不刪,舊的 .md 會留在 content/ 裡。
        // 不刪是刻意的(那正是 raw/ 與 media/ 安全的原因),但必須講出來——
        // 否則回報的 PagesWritten 與磁碟上的實際檔數從此對不起來,而沒有任何地方會提。
        RunPipeline();

        var trimmedRaw = Directory.CreateTempSubdirectory("polymigrate-trimmed").FullName;
        try
        {
            CopyDirectory(Path.Combine(FixtureDir, "raw"), trimmedRaw);
            File.Delete(Path.Combine(trimmedRaw, "ch", "news", "20260214.php.html"));

            var config = SiteConfigLoader.LoadFile(Path.Combine(FixtureDir, "config.yaml"));
            var report = new ExtractionPipeline(config).Run(new ExtractionPaths(
                trimmedRaw, Path.Combine(FixtureDir, "media"), _out));

            Assert.Equal(["content/ch/news/20260214.md"], report.StaleOutputs);
            Assert.True(File.Exists(Path.Combine(_out, "content", "ch", "news", "20260214.md")),
                "回報殘檔,但不得刪除它");
        }
        finally
        {
            Directory.Delete(trimmedRaw, recursive: true);
        }
    }

    [Fact]
    public void StaleOutputs_OnlyMarkdown_SortedOrdinal()
    {
        // 殘檔清單是 CLI 摘要的 [stale] 那幾行(只印前 10 筆),順序必須決定性(§3.10):
        // 用 ordinal 而不是檔案系統的列舉順序(NTFS 不分大小寫字母序、ext4 依雜湊)
        foreach (var name in (string[])["content/zz/a.md", "content/zz/B.md", "content/notes.txt"])
        {
            var path = Path.Combine(_out, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "left over");
        }

        var report = RunPipeline();

        Assert.Equal(["content/zz/B.md", "content/zz/a.md"], report.StaleOutputs);   // 'B' < 'a'(ordinal)
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    [Fact]
    public void FixtureSite_VerifyIsCleanExceptKnownMissing()
    {
        RunPipeline();

        var verify = new OutputVerifier().Run(_out, Path.Combine(FixtureDir, "media"));

        // 唯一 warning = 原站壞圖(broken.jpg,已記錄於 missing_images.csv);0 error = 巡檢乾淨
        Assert.Equal(0, verify.Errors);
        var warning = Assert.Single(verify.Issues);
        Assert.Equal("known_missing_media", warning.Kind);
        Assert.Equal("/media/images/broken.jpg", warning.Detail);
    }

    private void UpdateGolden()
    {
        if (Directory.Exists(GoldenDir))
        {
            Directory.Delete(GoldenDir, recursive: true);
        }
        foreach (var rel in RelativeFiles(_out).Where(r => r != "verify_report.csv"))
        {
            var target = Path.Combine(GoldenDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(_out, rel), target);
        }
    }

    private static List<string> RelativeFiles(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => f != "verify_report.csv" && !f.StartsWith(".polymigrate/", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)];

    /// <summary>
    /// CSV 的行尾是 CRLF(契約),golden 基準存的是 LF,所以比對前刪掉 \r;位元組格式另由 OutputBytesTests 釘。
    /// Markdown 不刪:它的契約就是一律 LF,刪了的話 Windows 上 frontmatter 混進 CRLF 也照樣全綠(踩過)。
    /// </summary>
    private static string Normalize(string relativePath, string text) =>
        relativePath.EndsWith(".md", StringComparison.Ordinal) ? text : text.Replace("\r", "");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PolyMigrate.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        }
        return dir;
    }
}
