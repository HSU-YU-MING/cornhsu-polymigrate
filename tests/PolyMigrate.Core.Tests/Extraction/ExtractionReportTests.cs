using PolyMigrate.Core.Diagnostics;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>
/// <see cref="ExtractionReport"/> 的 HasErrors / HasWarnings 直接決定 extract 的 exit code
/// (CLI:error → 2、warning → 1),所以每一個來源都要單獨釘住——
/// 少算一種 warning 等於讓壞掉的搬遷回 exit 0,多算一種等於讓既有使用者的綠燈無故變紅。
/// </summary>
public class ExtractionReportTests
{
    private static ExtractionReport Report(
        int missing = 0, int needFetch = 0, List<PathIssue>? issues = null, List<string>? stale = null) => new()
        {
            TypeCounts = [],
            FlagCounts = [],
            OnlyInLocale = [],
            MissingImages = missing,
            NeedFetchMedia = needFetch,
            PathIssues = issues ?? [],
            StaleOutputs = stale ?? [],
        };

    [Fact]
    public void Clean_HasNeitherErrorsNorWarnings()
    {
        var report = Report();

        Assert.False(report.HasErrors);
        Assert.False(report.HasWarnings);
        Assert.Equal(0, report.PagesSkippedUnsafe);
    }

    [Theory]
    [InlineData(1, 0)]   // 原站壞圖(missing_images.csv)
    [InlineData(0, 1)]   // 待補媒體(need_fetch_media.txt)
    public void MissingOrNeedFetchMedia_EachAloneIsAWarning(int missing, int needFetch)
    {
        var report = Report(missing, needFetch);

        Assert.True(report.HasWarnings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void PathWarning_AloneIsAWarning_NotAnError()
    {
        var report = Report(issues: [new PathIssue(Severity.Warning, "content/x.md", "path length 300 ...")]);

        Assert.True(report.HasWarnings);
        Assert.False(report.HasErrors);
        Assert.Equal(0, report.PagesSkippedUnsafe);   // 超長路徑照寫,不算跳過
    }

    [Fact]
    public void PathErrors_AreErrors_AndCountAsSkippedPages()
    {
        var report = Report(issues:
        [
            new PathIssue(Severity.Error, "content/con.md", "reserved device name 'con.md'"),
            new PathIssue(Severity.Error, "content/a.md", "duplicate output path"),
            new PathIssue(Severity.Warning, "content/long.md", "path length 300 ..."),
        ]);

        Assert.True(report.HasErrors);
        Assert.Equal(2, report.PagesSkippedUnsafe);
    }

    [Fact]
    public void StaleOutputs_DoNotCountAsWarnings()
    {
        // 2.2.1 刻意的取捨:殘檔只回報,不計入 warning——否則所有有殘檔的既有使用者
        // 的 extract 會在 patch 版從 exit 0 變 1(exit code 是對外契約)
        var report = Report(stale: ["content/ch/news/gone.md"]);

        Assert.False(report.HasWarnings);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void ForRoot_IsTheDocumentedDefaultLayout()
    {
        // root/raw、root/media,輸出直接到 root(content/ 與清單)——CLI 與 README 都照這個佈局寫
        var root = Path.Combine("some", "site");

        var paths = ExtractionPaths.ForRoot(root);

        Assert.Equal(Path.Combine(root, "raw"), paths.RawDir);
        Assert.Equal(Path.Combine(root, "media"), paths.MediaDir);
        Assert.Equal(root, paths.OutDir);
    }
}
