using PolyMigrate.Cli;

// Console 是行程級全域,重導向不能並行 → 關掉這個組件的平行測試
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PolyMigrate.Cli.Tests;

public class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("polymigrate-cli").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Task<(int Exit, string Out, string Err)> Run(params string[] args) =>
        RunWith(CancellationToken.None, args);

    private static async Task<(int Exit, string Out, string Err)> RunWith(
        CancellationToken ct, params string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var so = new StringWriter();
        var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var exit = await Cli.RunAsync(args, ct);
            return (exit, so.ToString(), se.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Version_ReturnsZero()
    {
        var r = await Run("--version");
        Assert.Equal(0, r.Exit);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_ReturnsZero(string flag)
    {
        var r = await Run(flag);
        Assert.Equal(0, r.Exit);
        Assert.Contains("Usage:", r.Out);
    }

    [Fact]
    public async Task NoArgs_ShowsHelp_ReturnsZero()
    {
        var r = await Run();
        Assert.Equal(0, r.Exit);
        Assert.Contains("Usage:", r.Out);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsTwo()
    {
        var r = await Run("frobnicate");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Unknown command", r.Err);
    }

    // §3.8 契約 + 1.1.2 修正:選項值不得吞掉後面的旗標
    [Fact]
    public async Task Extract_OptionValueSwallowingFlag_IsRejected()
    {
        var r = await Run("extract", "site.yaml", "--root", "--dry-run");
        Assert.Equal(2, r.Exit);
        Assert.Contains("--root requires a value", r.Err);
    }

    [Fact]
    public async Task Extract_MissingConfig_ReturnsUsage()
    {
        var r = await Run("extract");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Usage: polymigrate extract", r.Err);
    }

    [Fact]
    public async Task Extract_NonexistentConfig_ReturnsTwo()
    {
        var r = await Run("extract", Path.Combine(_dir, "nope.yaml"));
        Assert.Equal(2, r.Exit);
        Assert.Contains("Config file not found", r.Err);
    }

    [Fact]
    public async Task Verify_MissingOutputDir_ReturnsUsage()
    {
        var r = await Run("verify");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Usage: polymigrate verify", r.Err);
    }

    [Fact]
    public async Task Verify_UnexpectedFlag_ReturnsTwo()
    {
        var r = await Run("verify", "out", "--bogus");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Unexpected argument", r.Err);
    }

    [Fact]
    public async Task Thumbs_MissingValueForOption_ReturnsTwo()
    {
        var r = await Run("thumbs", "site.yaml", "--media");
        Assert.Equal(2, r.Exit);
        Assert.Contains("--media requires a value", r.Err);
    }

    [Theory]
    [InlineData("2023-2021")]   // from > to
    [InlineData("abc")]         // not a number
    [InlineData("20-21-22")]    // too many parts
    public async Task ProbeOrphans_InvalidYears_ReturnsTwo(string years)
    {
        var r = await Run("probe-orphans", "site.yaml", "--section", "news", "--years", years);
        Assert.Equal(2, r.Exit);
        Assert.Contains("Invalid --years", r.Err);
    }

    [Fact]
    public async Task ProbeOrphans_MissingRequiredOption_ReturnsUsage()
    {
        // --years 缺失(--section 有):應回 usage
        var r = await Run("probe-orphans", "site.yaml", "--section", "news");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Usage: polymigrate probe-orphans", r.Err);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // exit code 契約(§3.8):0 = 乾淨、1 = 有 warning、2 = 有 error、130 = 被中斷。
    //
    // 這組測試存在的理由:這四個值是「對外契約」,而且是最多人依賴的那一份——
    // CI 與 shell 腳本(set -e)直接吃它。在此之前,契約只寫在 Cli 與 Severity 的
    // 註解裡,沒有任何測試會因為有人改動它而變紅;新增一種 warning 就足以讓既有
    // 使用者的綠燈變紅燈,而我們不會事先知道。動 verify 的判定之前,先讓這裡有網。
    // ─────────────────────────────────────────────────────────────────────────

    private const string MinimalConfig =
        """
        config_version: 1
        site:
          base_url: https://example.invalid
          polite:
            delay_ms: 0
        url_pattern:
          lang_map:
            ch: zh-Hant
          default_lang: zh-Hant
        extract:
          content: "#main"
        """;

    /// <summary>造一份最小但合法的 Phase 2 輸出;body 決定這次要觸發哪一類巡檢結果。</summary>
    private string WriteOutput(string body, bool withMediaDir = false, string? knownMissing = null)
    {
        var outDir = Path.Combine(_dir, "out");
        Directory.CreateDirectory(Path.Combine(outDir, "content"));
        File.WriteAllText(Path.Combine(outDir, "content", "index.md"),
            $"""
            ---
            source_url: https://example.invalid/index.php
            lang: zh-Hant
            slug: index
            translation_key: /index
            title: 首頁
            page_type: page
            ---

            {body}

            """);
        if (withMediaDir)
        {
            // media 目錄不存在時巡檢會跳過媒體檢查——要驗媒體警告就必須讓它存在
            Directory.CreateDirectory(Path.Combine(outDir, "media"));
        }
        if (knownMissing is not null)
        {
            File.WriteAllText(Path.Combine(outDir, "missing_images.csv"),
                $"source_page,missing_image\r\ncontent/index.md,{knownMissing}\r\n");
        }
        return outDir;
    }

    [Fact]
    public async Task Verify_CleanOutput_ReturnsZero()
    {
        var outDir = WriteOutput("# 首頁\n\n這頁沒有任何引用。");

        var r = await Run("verify", outDir);

        Assert.Equal(0, r.Exit);
        Assert.Contains("errors          : 0", r.Out);
        Assert.Contains("warnings        : 0", r.Out);
    }

    [Fact]
    public async Task Verify_WarningOnly_ReturnsOne()
    {
        // 原站就壞掉的圖(已記在 missing_images.csv)= 已知、非搬遷回歸 → warning
        var outDir = WriteOutput(
            "![圖](/media/gone.jpg)", withMediaDir: true, knownMissing: "/media/gone.jpg");

        var r = await Run("verify", outDir);

        Assert.Equal(1, r.Exit);
        Assert.Contains("errors          : 0", r.Out);
        Assert.Contains("warnings        : 1", r.Out);
    }

    [Fact]
    public async Task Verify_Error_ReturnsTwo()
    {
        var outDir = WriteOutput("[壞連結](/nope)");

        var r = await Run("verify", outDir);

        Assert.Equal(2, r.Exit);
        Assert.Contains("broken_link", r.Out);
    }

    /// <summary>頂層目錄索引、沒有任何頁連到它——香雲寺 BLIA 那一類。</summary>
    private static void AddOrphanPage(string outDir)
    {
        var dir = Path.Combine(outDir, "content", "ch", "blia");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.md"),
            """
            ---
            source_url: https://example.invalid/ch/blia/index.php
            lang: zh-Hant
            slug: index
            translation_key: /blia/index
            title: BLIA
            page_type: page
            ---

            沒有任何頁連到這裡。

            """);
    }

    [Fact]
    public async Task Verify_OrphanPage_WarnsWithDetail()
    {
        var outDir = WriteOutput("# 首頁");
        AddOrphanPage(outDir);

        var r = await Run("verify", outDir);

        Assert.Equal(1, r.Exit);
        // 只給 warning 計數不夠:拿到非零 exit code 的人必須在畫面上看得到是哪一頁
        Assert.Contains("[warning] ch/blia/index.md: unlinked_page", r.Out);
    }

    [Fact]
    public async Task Verify_AllowUnlinkedFile_SuppressesOrphan()
    {
        var outDir = WriteOutput("# 首頁");
        AddOrphanPage(outDir);
        var allowFile = Path.Combine(_dir, "allow_unlinked.txt");
        File.WriteAllText(allowFile, "# 刻意不連的到達頁\nch/blia/index.md\n");

        var r = await Run("verify", outDir, "--allow-unlinked", allowFile);

        Assert.Equal(0, r.Exit);
    }

    [Fact]
    public async Task Verify_AllowUnlinkedFileMissing_ReturnsTwo()
    {
        var outDir = WriteOutput("# 首頁");

        var r = await Run("verify", outDir, "--allow-unlinked", Path.Combine(_dir, "nope.txt"));

        Assert.Equal(2, r.Exit);
        Assert.Contains("Allow-unlinked list not found", r.Err);
    }

    [Fact]
    public async Task Cancelled_Returns130()
    {
        // 已取消的 token:probe 在第一次 polite delay 就中止,不會發出任何請求
        var configPath = Path.Combine(_dir, "site.yaml");
        File.WriteAllText(configPath, MinimalConfig);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var r = await RunWith(cts.Token,
            "probe-orphans", configPath, "--section", "news", "--years", "2020");

        Assert.Equal(130, r.Exit);
        Assert.Contains("cancelled", r.Err);
    }
}
