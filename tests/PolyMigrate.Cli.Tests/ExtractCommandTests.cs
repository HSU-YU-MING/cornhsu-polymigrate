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
        Assert.Contains("  flags            : needs_rebuild=1, text_in_image=4\n", r.Out);
        Assert.Contains("  single-locale    : en=2, zh-Hant=3\n", r.Out);
        Assert.Contains("  suggested pairs  : 2 (heuristic, review in content_inventory.csv)\n", r.Out);
        Assert.Contains("  media referenced : 4\n", r.Out);
        Assert.Contains("  stale in content : 0 (present but not written by this run)\n", r.Out);
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
    public async Task Cancelled_Returns130_BeforeWritingAnyPage()
    {
        // 原本同步指令不看 token:第一次 Ctrl-C 沒作用、照常跑完回 0/1/2;第二次由 runtime 直接終止,
        // 不經 Cli.Report(),Windows 上的結束碼是 0xC000013A 而不是契約的 130
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var r = await RunWith(cts.Token, "extract", config);

        Assert.Equal(130, r.Exit);
        Assert.Equal("cancelled.\n", r.Err);
        Assert.Empty(r.Out);
        Assert.False(Directory.Exists(PathOf("site/content")));
    }

    [Fact]
    public async Task InternalHashCacheCannotBeSaved_ExitCodeIsUnchanged()
    {
        // 內部加速檔(.polymigrate/media_sha1_cache.csv)存不了只會讓下次變慢,不可以讓 exit 由 0 變 2
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/news/a.php.html", Page("<p>夠長的文字讓這頁不會被標成資訊鎖在圖裡的頁面。</p><img src=\"/img/a.jpg\">"));
        Write("site/media/img/a.jpg", "jpeg");
        Write("site/.polymigrate", "a file where the cache directory should be");

        var r = await Run("extract", config);

        Assert.Equal(0, r.Exit);
        Assert.Empty(r.Err);
        Assert.True(File.Exists(PathOf("site/need_fetch_media.txt")));
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

    [Theory]
    [InlineData("extract")]
    [InlineData("thumbs")]   // thumbs 根本不用 strip_title_prefix:2.x 照樣 exit 0
    public async Task InvalidRegexInStripTitlePrefix_ExitsTwo_AtLoad_NamingTheField(string command)
    {
        // 2.x 載入時不驗 regex:extract 要到建標題清理器時才 exit 2(「error: Invalid pattern …」,沒點名欄位),
        // 不用它的指令永遠不報。3.0 起載入時就擋,訊息點名欄位與 pattern
        var config = Write("site/site.yaml", Config(extraExtract: "  strip_title_prefix: ['(']"));
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        Write("site/media/a.jpg", "not used");

        var r = await Run(command, config);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"{config}: config is invalid:\n"
            + "  - extract.strip_title_prefix: '(' is not a valid regular expression (InsufficientClosingParentheses at offset 1).\n", r.Err);
        Assert.Empty(r.Out);
        Assert.False(Directory.Exists(PathOf("site/content")));
        Assert.False(Directory.Exists(PathOf("site/media_thumb")));
    }

    [Fact]
    public async Task ConfigWarning_IsPrintedOnStderr_WithoutChangingTheExitCode()
    {
        // 載入時的 warning(例如用不到的 pdf_labels)走既有的 stderr 慣例「warning: …」,不算 extract 的 warning、不影響 exit code
        var config = Write("site/site.yaml", Config(extraTop: "media:\n  pdf_labels: { ja: PDF を表示 }"));
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var r = await Run("extract", config);

        Assert.Equal(0, r.Exit);
        Assert.Equal($"warning: {config}: media.pdf_labels key 'ja' is not one of the lang_map locales (zh-Hant); that label is never used.\n", r.Err);
    }

    [Fact]
    public async Task ConfigKeyWithNoValue_ExitsTwo_InsteadOfCrashing()
    {
        // YAML 寫了 key 卻沒給值 → null。2.x 在載入或使用時 NullReferenceException:未處理例外,exit code 不在契約內
        var config = Write("site/site.yaml", Config(extraExtract: "  title_noise:"));
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"{config}: config is invalid:\n"
            + "  - extract.title_noise has no value (write [] for an empty list, or remove the key for the default).\n", r.Err);
    }

    [Fact]
    public async Task InvalidContentSelector_ExitsTwo_NamingTheField_WritingNothing()
    {
        // 寫壞的 selector 原本讓 AngleSharp 的 DomException 直接穿出去,以未處理例外結束
        // (Windows 為 0xE0434352),不在 0/1/2/130 契約內。3.0 起在載入 config 時就擋
        var config = Write("site/site.yaml", Config().Replace("content: \"main\"", "content: \"section[id\""));
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"{config}: config is invalid:\n  - extract.content: 'section[id' is not a valid CSS selector.\n", r.Err);
        Assert.Empty(r.Out);
        Assert.False(Directory.Exists(PathOf("site/content")));
        Assert.False(File.Exists(PathOf("site/content_inventory.csv")));
    }

    [Theory]
    [InlineData("title_selector: \"h3[[\"", "extract.type_rules.article.title_selector: 'h3[['")]
    [InlineData("remove_selectors: [\"h5\", \"h5[[\"]", "extract.type_rules.article.remove_selectors: 'h5[['")]
    public async Task InvalidTypeRuleSelector_ExitsTwo_BeforeAnyPageIsWritten(string rule, string message)
    {
        // type_rules 的 selector 只在碰到該頁型時才用到:原本 ch/index(page 型,字典序在前)
        // 已經寫進 content/,清單檔卻一份都沒寫,就崩潰了
        var config = Write("site/site.yaml", Config(extraExtract:
            $"  section_types: {{ news: article }}\n  type_rules:\n    article:\n      {rule}"));
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));
        Write("site/raw/ch/news/a.php.html", Page("<h3>標題</h3><p>內文</p>"));

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"{config}: config is invalid:\n  - {message} is not a valid CSS selector.\n", r.Err);
        Assert.False(Directory.Exists(PathOf("site/content")));
    }

    [Fact]
    public async Task InvalidSelectorOfAnUnusedPageType_ExitsTwo_AtLoad()
    {
        // 2.x 刻意不擋:寫壞但這次沒有任何頁用到的規則照樣 exit 0(只為了不讓原本能跑的 config 在 patch 版失敗)。
        // 3.0 起載入時就驗:寫壞的 config 不該等到哪天出現那個頁型才炸
        var config = Write("site/site.yaml", Config(extraExtract:
            "  section_types: { events: event }\n  type_rules:\n    event:\n      title_selector: \"h3[[\""));
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var r = await Run("extract", config);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"{config}: config is invalid:\n  - extract.type_rules.event.title_selector: 'h3[[' is not a valid CSS selector.\n", r.Err);
        Assert.Empty(r.Out);
        Assert.False(Directory.Exists(PathOf("site/content")));
    }

    [Theory]
    [InlineData("thumbs")]
    [InlineData("probe-orphans")]
    public async Task InvalidSelector_CommandsThatNeverUseSelectors_AlsoExitTwo_AtLoad(string command)
    {
        // thumbs 與 probe-orphans 根本不用 selector:2.x 照樣跑完。同一份 config 交給 extract 一定會炸,
        // 所以在哪個指令先讀到它就在哪裡擋,不發任何請求、不寫任何檔
        var config = Write("site/site.yaml", Config(baseUrl: "https://example.invalid").Replace("content: \"main\"", "content: \"main[\""));
        Write("site/media/a.jpg", "not used");

        var r = command == "thumbs"
            ? await Run("thumbs", config)
            : await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(2, r.Exit);
        Assert.Equal($"{config}: config is invalid:\n  - extract.content: 'main[' is not a valid CSS selector.\n", r.Err);
        Assert.Empty(r.Out);
        Assert.False(Directory.Exists(PathOf("site/media_thumb")));
        Assert.False(File.Exists(PathOf("site/orphan_slugs.news.txt")));
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
