using PolyMigrate.Core.Configuration;
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

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    [Fact]
    public void ReadmeLibraryExample_RunsAsWritten()
    {
        // README「Use as a library」那段照抄:FromConfigFile → Extract("out/") → Verify("out/")。
        // Extract(string) 走預設佈局(root/raw、root/media → root),Verify 不給 mediaDir 時
        // 用 root/media——兩個預設值都只有這個範例在用,沒有別的測試會因為它們改掉而變紅。
        // 三個目錄各有斷言在守:raw → PagesWritten;media → MediaReferenced / MissingImages
        // (媒體目錄指錯時圖全變成「不存在」:引用 0 張、壞圖變多);out → 檔案位置。
        CopyDirectory(Path.Combine(FixtureDir, "raw"), Path.Combine(_out, "raw"));
        CopyDirectory(Path.Combine(FixtureDir, "media"), Path.Combine(_out, "media"));

        var migrator = PolyMigrator.FromConfigFile(Path.Combine(FixtureDir, "config.yaml"));
        var report = migrator.Extract(_out);
        var verify = PolyMigrator.Verify(_out);

        Assert.False(report.HasErrors);
        Assert.Equal(13, report.PagesWritten);
        Assert.Equal(4, report.MediaReferenced);    // 與 FixtureReport_CountsMatchTheInventory 同一份 fixture
        Assert.Equal(2, report.MissingImages);      // fixture 刻意放的 2 張原站壞圖
        Assert.True(File.Exists(Path.Combine(_out, "content", "ch", "news", "20260101.md")));
        Assert.True(File.Exists(Path.Combine(_out, "content_inventory.csv")));

        // media 檢查有做(沒被跳過):唯一的 warning 是 missing_images.csv 已記錄的原站壞圖
        Assert.False(verify.MediaChecksSkipped);
        Assert.Equal(0, verify.Errors);
        Assert.Equal(1, verify.Warnings);
    }

    [Fact]
    public void FixtureReport_CountsMatchTheInventory()
    {
        // 這些是 CLI 摘要的 types / flags / single-locale / media referenced 那幾行的資料來源
        var report = new PolyMigrator(SiteConfigLoader.LoadFile(Path.Combine(FixtureDir, "config.yaml")))
            .Extract(Paths());

        Assert.Equal(new SortedDictionary<string, int> { ["article"] = 3, ["event"] = 2, ["listing"] = 1, ["page"] = 3 },
            report.TypeCounts);
        Assert.Equal(new SortedDictionary<string, int> { ["needs_rebuild"] = 1, ["text_in_image"] = 4 },
            report.FlagCounts);
        Assert.Equal(new SortedDictionary<string, int> { ["en"] = 2, ["zh-Hant"] = 3 }, report.OnlyInLocale);
        Assert.Equal(4, report.MediaReferenced);
        Assert.Empty(report.PathIssues);
        Assert.Equal(0, report.PagesSkippedUnsafe);
        Assert.True(report.HasWarnings);       // 2 張壞圖 + 1 份待補 PDF → CLI exit 1
        Assert.True(report.HreflangInFallback);
    }

    [Fact]
    public void CjkSlug_ExtractThenVerify_HasNoFalseFindings()
    {
        // CLAUDE.md「已知教訓」:中日韓 slug 的百分號編碼 href 對不上解碼後的鏡像檔名,
        // verify 報 broken_link、還連帶誤報 unlinked_page——「一個正確的連結產生兩筆假發現」。
        // 單元測試各自證明 extract 與 verify 對;這裡證明兩者**接在一起**也對:
        // extract 寫出的連結(編碼、去 .php)要能被 verify 對回 extract 寫出的檔(解碼檔名)
        var raw = Path.Combine(_out, "raw");
        Directory.CreateDirectory(Path.Combine(raw, "ch", "news"));
        File.WriteAllText(Path.Combine(raw, "ch", "index.php.html"),
            "<html><body><main><a href=\"news/%E7%A6%AA%E4%BF%AE.php\">禪修</a> <a href=\"news/浴佛.php\">浴佛</a></main></body></html>");
        File.WriteAllText(Path.Combine(raw, "ch", "news", "禪修.php.html"), "<html><body><main>禪修營</main></body></html>");
        File.WriteAllText(Path.Combine(raw, "ch", "news", "浴佛.php.html"), "<html><body><main>浴佛法會</main></body></html>");
        var config = SiteConfigLoader.Load(
            """
            config_version: 1
            site:
              base_url: https://old.example.org
            url_pattern:
              lang_map: { ch: zh-Hant }
              default_lang: zh-Hant
            extract:
              content: main
            """);

        var extract = new PolyMigrator(config).Extract(_out);
        var verify = PolyMigrator.Verify(_out);

        Assert.Equal(3, extract.PagesWritten);
        Assert.True(File.Exists(Path.Combine(_out, "content", "ch", "news", "禪修.md")));
        Assert.Equal(2, verify.LinksChecked);
        Assert.Empty(verify.Issues);   // 沒有 broken_link,也沒有 unlinked_page
    }

    [Fact]
    public void GenerateThumbnails_GoesThroughTheFacade()
    {
        var media = Path.Combine(_out, "media");
        Directory.CreateDirectory(media);
        using (var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.Blue, 1600, 800))
        {
            image.Write(Path.Combine(media, "wide.jpg"), ImageMagick.MagickFormat.Jpeg);
        }
        var config = SiteConfigLoader.LoadFile(Path.Combine(FixtureDir, "config.yaml"));
        config.Media.Thumbnails = new ThumbnailSection { MaxWidth = 400, Quality = 82 };

        var report = new PolyMigrator(config).GenerateThumbnails(media, Path.Combine(_out, "thumbs"));

        Assert.Equal(1, report.Created);
        using var thumb = new ImageMagick.MagickImage(Path.Combine(_out, "thumbs", "wide.jpg"));
        Assert.Equal((400u, 200u), (thumb.Width, thumb.Height));   // config 的 max_width 有傳到底
    }

    // ── 可取消的 overload(3.0):2.x 只有 CLI 接得到 Ctrl-C,函式庫呼叫端取消不了 ─────────────────
    // 用預先取消的 token:facade 若沒把 token 傳到底(例如傳 CancellationToken.None),這幾條會照常跑完而變紅。
    // 「半途取消、逐頁檢查」那一層在 CancellationTests 以內部的檢查點掛勾測過。

    private static CancellationToken Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Fact]
    public void Extract_Cancellable_StopsBeforeWritingAnything()
    {
        var migrator = PolyMigrator.FromConfigFile(Path.Combine(FixtureDir, "config.yaml"));
        CopyDirectory(Path.Combine(FixtureDir, "raw"), Path.Combine(_out, "raw"));   // root 版用預設佈局 root/raw

        Assert.ThrowsAny<OperationCanceledException>(() => migrator.Extract(Paths(), dryRun: false, Cancelled()));
        Assert.ThrowsAny<OperationCanceledException>(() => migrator.Extract(_out, dryRun: false, Cancelled()));

        Assert.False(Directory.Exists(Path.Combine(_out, "content")));
        Assert.False(File.Exists(Path.Combine(_out, "content_inventory.csv")));
    }

    [Fact]
    public void Verify_Cancellable_DoesNotWriteAHalfReport()
    {
        PolyMigrator.FromConfigFile(Path.Combine(FixtureDir, "config.yaml")).Extract(Paths());

        Assert.ThrowsAny<OperationCanceledException>(() =>
            PolyMigrator.Verify(_out, Path.Combine(FixtureDir, "media"), "/media/", null, Cancelled()));

        Assert.False(File.Exists(Path.Combine(_out, "verify_report.csv")));
    }

    [Fact]
    public void GenerateThumbnails_Cancellable_StopsBeforeTheFirstImage()
    {
        var migrator = PolyMigrator.FromConfigFile(Path.Combine(FixtureDir, "config.yaml"));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            migrator.GenerateThumbnails(Path.Combine(FixtureDir, "media"), Path.Combine(_out, "thumbs"), Cancelled()));

        Assert.False(Directory.Exists(Path.Combine(_out, "thumbs")));
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
