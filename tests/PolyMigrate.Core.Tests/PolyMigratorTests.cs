using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests;

/// <summary>函式庫 facade 的端到端測試:消費者只透過 PolyMigrator 就能跑完抽取 + 巡檢。</summary>
public class PolyMigratorTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(FindRepoRoot(), "tests", "fixtures", "site");
    private readonly string _out = Directory.CreateTempSubdirectory("polymigrate-facade").FullName;

    public void Dispose() => Directory.Delete(_out, recursive: true);

    private ExtractionPaths Paths() => new(
        Path.Combine(FixtureDir, "raw"), Path.Combine(FixtureDir, "media"), _out);

    [Fact]
    public void ExtractThenVerify_EndToEnd()
    {
        var migrator = PolyMigrator.FromConfigFile(Path.Combine(FixtureDir, "config.yaml"));

        var report = migrator.Extract(Paths());
        Assert.True(report.PagesWritten > 0);
        Assert.False(report.HasErrors);

        var verify = PolyMigrator.Verify(_out, Path.Combine(FixtureDir, "media"));
        Assert.Equal(0, verify.Errors);
    }

    [Fact]
    public void DryRun_WritesNothing()
    {
        var migrator = PolyMigrator.FromConfigFile(Path.Combine(FixtureDir, "config.yaml"));

        var report = migrator.Extract(Paths(), dryRun: true);

        Assert.True(report.PagesWritten > 0);
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

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
