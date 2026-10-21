namespace PolyMigrate.Cli.Tests;

/// <summary>
/// <c>polymigrate extract</c> 經過 CLI 的每一條結束路徑。exit code 是對外契約(§3.8):
/// 0 乾淨、1 有 warning(壞圖、待補媒體、超長路徑)、2 有 error(不安全路徑拒寫)或用法/config/IO 錯誤。
/// 在此之前 CliTests 只測了參數錯誤,成功路徑的 0/1/2 判定完全沒有經過 CLI 驗過。
/// </summary>
public class ExtractCommandTests : CliTestBase
{
    [Fact]
    public async Task FixtureSite_WarningsOnly_ExitsOne_WithSummary()
    {
        // fixture 刻意帶 2 張原站壞圖與 1 份待補 PDF → warning → exit 1;沒有不安全路徑
        var config = CopyFixtureSite();

        var r = await Run("extract", config);

        Assert.Equal(1, r.Exit);
        Assert.Contains("Phase 2 done.", r.Out);
        Assert.Contains("  markdown files   : 13\n", r.Out);
        Assert.Contains("  translation keys : 9\n", r.Out);
        Assert.Contains("  types            : article=3, event=2, listing=1, page=3\n", r.Out);
        Assert.Contains("  missing images   : 2 (recorded in missing_images.csv, non-blocking)\n", r.Out);
        Assert.Contains("  need-fetch media : 1 (recorded in need_fetch_media.txt)\n", r.Out);
        Assert.Contains("  path issues      : 0 (0 pages skipped as unsafe, see path_issues.csv)\n", r.Out);
        Assert.Contains("  hreflang links   : 6 declared, 2 passed validation (used for pairing;", r.Out);
        Assert.Empty(r.Err);

        // 預設 root = config 所在目錄:輸出寫在 config 旁邊
        Assert.True(File.Exists(PathOf("site/content/ch/news/20260101.md")));
        Assert.True(File.Exists(PathOf("site/content_inventory.csv")));
    }

    [Fact]
    public async Task CleanSite_ExitsZero()
    {
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var r = await Run("extract", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains("  markdown files   : 1\n", r.Out);
    }

    [Fact]
    public async Task HreflangNotInFallback_SummarySaysItWasNotUsed()
    {
        // CLAUDE.md「地雷」:hreflang 預設不啟用。查證照做、數字照印,但摘要必須講明這次沒拿它配對——
        // 只印「N passed validation」的話,看到數字的人會以為它有作用
        var config = Write("site/site.yaml", Config(langMap: "{ ch: zh-Hant, en: en }"));
        Write("site/raw/ch/news/a.php.html",
            "<html><head><link rel=\"alternate\" hreflang=\"en\" href=\"/en/news/b.php\"></head><body><main>中文</main></body></html>");
        Write("site/raw/en/news/b.php.html", Page("English"));

        var r = await Run("extract", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains(
            "  hreflang links   : 1 declared, 1 passed validation (NOT used — add 'hreflang' to pairing.fallback; why not: hreflang_map.csv)\n",
            r.Out);
        Assert.Contains("  suggested pairs  : 0 (heuristic, review in content_inventory.csv)\n", r.Out);
    }

    [Fact]
    public async Task OsJunkFilesInTheMirror_AreNotPages()
    {
        // 鏡像目錄被 Finder / 檔案總管開過會長出 .DS_Store、Thumbs.db;一個鏡像頁的定義是 *.html
        // (slugs 也用同一把尺,見 CLAUDE.md「已知教訓」)
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        Write("site/raw/ch/.DS_Store", "\0\0\0\u0001Bud1");
        Write("site/raw/ch/Thumbs.db", "junk");

        var r = await Run("extract", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains("  markdown files   : 1\n", r.Out);
        Assert.Equal(["index.md"], Directory.GetFiles(PathOf("site/content/ch")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task UnsafePath_ExitsTwo_EvenWhenThereAreAlsoWarnings()
    {
        // a.asp 與 a.php 都收斂成 content/ch/news/a.md → 後者拒寫(error);另有一張壞圖(warning)。
        // error 必須優先於 warning:2 不能被 1 蓋掉
        var config = Write("site/site.yaml", Config().Replace("default_lang: zh-Hant",
            "default_lang: zh-Hant\n  strip_extensions: [.php, .asp]"));
        Write("site/raw/ch/news/a.asp.html", Page("<img src=\"/gone.jpg\">"));
        Write("site/raw/ch/news/a.php.html", Page("text"));

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.Contains("  path issues      : 1 (1 pages skipped as unsafe, see path_issues.csv)\n", r.Out);
        Assert.Contains(
            "  [error] content/ch/news/a.md: duplicate output path (another source also maps to 'content/ch/news/a.md')\n",
            r.Out);
    }

    [Fact]
    public async Task DryRun_ReportsButWritesNothing()
    {
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        var outDir = PathOf("never-created");

        var r = await Run("extract", config, "--dry-run", "--out", outDir);

        Assert.Equal(0, r.Exit);
        Assert.Contains("Phase 2 dry-run done (nothing written).", r.Out);
        Assert.Contains("  markdown files   : 1\n", r.Out);
        Assert.False(Directory.Exists(outDir));   // 連輸出目錄都不建
        Assert.Equal(["raw", "site.yaml"],
            Directory.GetFileSystemEntries(PathOf("site")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DryRun_KeepsTheSameExitCode()
    {
        // dry-run 的用途就是「先看會不會過」——exit code 必須與真的跑一樣,否則 CI 預檢沒有意義
        var config = CopyFixtureSite();

        var r = await Run("extract", config, "--dry-run");

        Assert.Equal(1, r.Exit);
        Assert.False(Directory.Exists(PathOf("site/content")));
        Assert.False(File.Exists(PathOf("site/content_inventory.csv")));
    }

    [Fact]
    public async Task RawMediaOutOverrides_AreAllHonored()
    {
        var config = Write("cfg/site.yaml", Config());
        Write("mirror/ch/news/a.php.html", Page("<p>夠長的文字讓這頁不會被標成資訊鎖在圖裡的頁面。</p><img src=\"/img/a.jpg\">"));
        Write("assets/img/a.jpg", "jpeg");
        var outDir = PathOf("dist");

        var r = await Run("extract", config,
            "--raw", PathOf("mirror"), "--media", PathOf("assets"), "--out", outDir);

        // 圖在 --media 指定的目錄裡找得到 → 沒有壞圖 → 乾淨
        Assert.Equal(0, r.Exit);
        Assert.Contains("  media referenced : 1\n", r.Out);
        Assert.True(File.Exists(Path.Combine(outDir, "content", "ch", "news", "a.md")));
        Assert.True(File.Exists(Path.Combine(outDir, "media_manifest.csv")));
        Assert.Equal(["site.yaml"], Directory.GetFileSystemEntries(PathOf("cfg")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RootOverride_ReplacesTheConfigDirectoryAsRoot()
    {
        var config = Write("cfg/site.yaml", Config());
        Write("work/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var r = await Run("extract", config, "--root", PathOf("work"));

        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(PathOf("work/content/ch/index.md")));
        Assert.False(Directory.Exists(PathOf("cfg/content")));
    }

    [Fact]
    public async Task RawMirrorMissing_ExitsTwo()
    {
        var config = Write("site/site.yaml", Config());

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"Raw mirror directory not found: {PathOf("site/raw")}\n", r.Err);
    }

    [Fact]
    public async Task OverlongOutputPath_IsAWarning_ExitsOne()
    {
        // 超長路徑(> 傳統 Windows 260)照寫但記為 warning;dry-run 就能判出來,不必真的建出長路徑
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        var longOut = Path.Combine(Root, new string('o', 120), new string('p', 120));

        var r = await Run("extract", config, "--dry-run", "--out", longOut);

        Assert.Equal(1, r.Exit);
        Assert.Contains("  path issues      : 1 (0 pages skipped as unsafe, see path_issues.csv)\n", r.Out);
        Assert.Contains("  [warning] content/ch/index.md: path length ", r.Out);
    }

    [Fact]
    public async Task StaleOutputs_AreReported_ButDoNotChangeTheExitCode()
    {
        // 2.2.1 的刻意取捨:殘檔只回報、不計 warning——否則有殘檔的既有使用者在 patch 版從 0 變 1
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        var gone = Write("site/raw/ch/news/gone.php.html", Page("<p>要下架的文章</p>"));
        Assert.Equal(0, (await Run("extract", config)).Exit);
        File.Delete(gone);

        var r = await Run("extract", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains("  stale in content : 1 (present but not written by this run)\n", r.Out);
        Assert.Contains("  [stale] content/ch/news/gone.md\n", r.Out);
        Assert.True(File.Exists(PathOf("site/content/ch/news/gone.md")));   // 只寫不刪
    }

    [Fact]
    public async Task InvalidRegexInStripTitlePrefix_ExitsTwo_WithMessage()
    {
        // config 載入時不驗 regex;壞掉的 pattern 在抽取時才爆——必須仍是乾淨的 exit 2,不是崩潰
        var config = Write("site/site.yaml", Config(extraExtract: "  strip_title_prefix: ['(']"));
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("error: ", r.Err);   // 訊息本文來自 .NET 的 RegexParseException,不綁字面
        Assert.Empty(r.Out);
    }

    [Fact]
    public async Task InvalidConfig_ExitsTwo_PrintingTheLoaderMessageVerbatim()
    {
        // ConfigException 的訊息本身就是給使用者看的,不加「error:」前綴
        var config = Write("site/site.yaml", Config(extraTop: "extrat:\n  content: x"));

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.StartsWith($"{config}: invalid config — ", r.Err);
        Assert.Contains("extrat", r.Err);
    }

    [Fact]
    public async Task OutputDirectoryIsAFile_ExitsTwo()
    {
        // IO 錯誤一律 exit 2 + 「error: 訊息」,而不是未處理例外
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        var blocker = Write("site/out", "a file, not a directory");

        var r = await Run("extract", config, "--out", blocker);

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("error: ", r.Err);
    }

    [Theory]
    [InlineData("second.yaml")]   // 第二個位置參數
    [InlineData("--bogus")]       // 未知旗標
    public async Task UnexpectedArgument_ExitsTwo(string extra)
    {
        var r = await Run("extract", "site.yaml", extra);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"Unexpected argument: {extra}\n", r.Err);
    }

    [Theory]
    [InlineData("--raw")]
    [InlineData("--media")]
    [InlineData("--out")]
    public async Task PathOptionWithoutValue_ExitsTwo(string option)
    {
        var r = await Run("extract", "site.yaml", option);

        Assert.Equal(2, r.Exit);
        Assert.Contains($"Option {option} requires a value.", r.Err);
    }
}
