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
            var expected = Normalize(File.ReadAllText(Path.Combine(GoldenDir, rel)));
            var actual = Normalize(File.ReadAllText(Path.Combine(_out, rel)));
            Assert.True(expected == actual, $"golden mismatch: {rel}\n--- expected ---\n{expected}\n--- actual ---\n{actual}");
        }
    }

    [Fact]
    public void FixtureSite_ReportMatchesSpec()
    {
        var report = RunPipeline();

        Assert.Equal(13, report.PagesWritten);
        Assert.Equal(9, report.TranslationKeys);
        Assert.Equal(2, report.SuggestedPairs);        // date 組 + shared_media 組
        Assert.Equal(2, report.MissingImages);         // 20260101_3.jpg + broken.jpg
        Assert.Equal(1, report.NeedFetchMedia);        // schedule.pdf
        Assert.Empty(report.StaleOutputs);             // 乾淨的一次跑完,content/ 裡不該有多餘的檔
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

    private static string Normalize(string text) => text.Replace("\r", "");

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
