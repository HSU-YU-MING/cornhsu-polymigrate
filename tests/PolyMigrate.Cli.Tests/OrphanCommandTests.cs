using System.Text;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// <c>probe-orphans</c> / <c>fetch-orphans</c> 的完整 CLI 路徑(預設值、檔案格式、exit code 0/1/2/130)。
/// CLI 自己建 HttpClient、沒有注入點,所以對著 <see cref="LoopbackHttpServer"/>(127.0.0.1、不經 DNS)跑。
/// </summary>
public class OrphanCommandTests : CliTestBase
{
    private string SiteConfig(string baseUrl, string langMap = "{ ch: zh-Hant, en: en }") =>
        Write("site/site.yaml", Config(baseUrl: baseUrl, langMap: langMap));

    // ── probe-orphans ───────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_WritesSlugFile_LfSeparated_NoTrailingNewline_NoBom()
    {
        // orphan_slugs.<section>.txt 是 fetch-orphans --slugs 的輸入,也常被 diff / wc -l 處理:格式要釘死
        await using var server = new LoopbackHttpServer((_, path) =>
            path is "/ch/news/20200101.php" or "/ch/news/20200101A.php" or "/ch/news/12312020.php"
                ? new LoopbackHttpServer.Response(200)
                : LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl);

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(0, r.Exit);
        var slugFile = PathOf("site/orphan_slugs.news.txt");   // 預設 <root>/orphan_slugs.<section>.txt
        Assert.Equal("20200101\n20200101A\n12312020"u8.ToArray(), File.ReadAllBytes(slugFile));
        Assert.Contains($"Probing {server.BaseUrl}/ch/news/ for 2020-2020 (0 known slugs skipped, delay 0ms)...\n", r.Out);
        Assert.Contains($"Probe done: 3 orphan slugs -> {slugFile}\n", r.Out);
        Assert.Contains("hit 20200101A\n", r.Out);   // 進度回呼接到 stdout
        // 2020 是閏年:366 天 × 2 種格式,再加命中後的 A(命中)、B(未命中)與 12312020 的 A(未命中)
        Assert.Equal(366 * 2 + 3, server.Requests.Count);
        Assert.All(server.Requests, q => Assert.StartsWith("HEAD /ch/news/", q));
    }

    [Fact]
    public async Task Probe_DefaultLang_IsTheFirstLangMapKey()
    {
        // lang_map 的順序有意義:沒給 --lang 時探第一個 key(這裡是 en,不是字典序的 ch)
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl, langMap: "{ en: en, ch: zh-Hant }");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2021");

        Assert.Equal(0, r.Exit);
        Assert.Contains($"Probing {server.BaseUrl}/en/news/", r.Out);
        Assert.All(server.Requests, q => Assert.StartsWith("HEAD /en/news/", q));
        Assert.Equal(""u8.ToArray(), File.ReadAllBytes(PathOf("site/orphan_slugs.news.txt")));   // 0 筆 = 空檔
    }

    [Fact]
    public async Task Probe_KnownSlugs_FollowTheConfigsStripExtensions()
    {
        // strip_extensions: [] 的站網址沒有副檔名:鏡像裡的 20210105.php.html 是另一個網址(/news/20210105.php),
        // /news/20210105 本身並沒有鏡像,所以要探。不帶 config 的啟發式會把它剝成 20210105、當成已知而跳過
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response.NotFound);
        var config = Write("site/site.yaml", Config(baseUrl: server.BaseUrl)
            .Replace("default_lang: zh-Hant", "default_lang: zh-Hant\n  strip_extensions: []"));
        Write("site/raw/ch/news/20210105.php.html", "<html></html>");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2021");

        Assert.Equal(0, r.Exit);
        Assert.Contains("(1 known slugs skipped, delay 0ms)", r.Out);   // 已知的是 20210105.php,不是任何日期候選
        Assert.Contains("HEAD /ch/news/20210105", server.Requests);
    }

    [Fact]
    public async Task Probe_LangRootAndOut_Overrides_AndKnownSlugsAreSkipped()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response.NotFound);
        var config = Write("cfg/site.yaml", Config(baseUrl: server.BaseUrl, langMap: "{ ch: zh-Hant, en: en }"));
        // --root 決定「已抓過」從哪裡讀:<root>/raw/<lang>/<section>
        Write("work/raw/en/news/01012021.php.html", "<html></html>");
        Write("work/raw/en/news/20210101.php.html", "<html></html>");
        var outFile = PathOf("lists/en.txt");
        Directory.CreateDirectory(PathOf("lists"));

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2021-2021",
            "--lang", "en", "--root", PathOf("work"), "--out", outFile);

        Assert.Equal(0, r.Exit);
        Assert.Contains("(2 known slugs skipped, delay 0ms)", r.Out);
        // 已鏡像的日期本身不探,但它的 A 後綴照探(後綴是不是孤兒與原頁有沒有鏡像無關)
        Assert.DoesNotContain("HEAD /en/news/01012021.php", server.Requests);
        Assert.DoesNotContain("HEAD /en/news/20210101.php", server.Requests);
        Assert.Contains("HEAD /en/news/01012021A.php", server.Requests);
        Assert.Equal(365 * 2, server.Requests.Count);
        Assert.True(File.Exists(outFile));
        Assert.False(File.Exists(PathOf("work/orphan_slugs.news.txt")));
    }

    [Fact]
    public async Task Probe_CancelledMidway_Returns130_AndKeepsThePreviousSlugFile()
    {
        // Ctrl-C 要乾淨收尾(130),而且不可以把上一次辛苦探到的清單覆寫成半份或空的
        using var cts = new CancellationTokenSource();
        var seen = 0;
        await using var server = new LoopbackHttpServer((_, _) =>
        {
            if (Interlocked.Increment(ref seen) == 10)
            {
                cts.Cancel();
            }
            return LoopbackHttpServer.Response.NotFound;
        });
        var config = SiteConfig(server.BaseUrl);
        var slugFile = Write("site/orphan_slugs.news.txt", "20190101\n20190102");

        var r = await RunWith(cts.Token, "probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(130, r.Exit);
        Assert.Equal("cancelled.\n", r.Err);
        Assert.Equal("20190101\n20190102", File.ReadAllText(slugFile));
        Assert.InRange(server.Requests.Count, 10, 11);   // 取消後不再發新請求
    }

    [Fact]
    public async Task NoLangPrefixSite_ProbeAndFetch_RequestSingleSlashUrls()
    {
        // lang_map 的 key 是 "" = 沒有語言前綴的站(合法設定,見 MirrorSlugs 與 CHANGELOG 2.2.0)。
        // 原本 $"{base}/{lang}/{section}/…" 組出 //news/…:不合併斜線的伺服器或框架路由會讓 probe
        // 全部落空、fetch 全部 404(nginx 預設合併斜線,所以不是每個站都中)
        await using var server = new LoopbackHttpServer((_, path) => path switch
        {
            "/news/20200101.php" => LoopbackHttpServer.Response.Html(OrphanPage),
            "/news/images/a.jpg" => new LoopbackHttpServer.Response(200, "image/jpeg", [1, 2, 3]),
            _ => LoopbackHttpServer.Response.NotFound,
        });
        var config = SiteConfig(server.BaseUrl, langMap: "{ \"\": zh-Hant }");

        var probe = await Run("probe-orphans", config, "--section", "news", "--years", "2020");
        var fetch = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(0, probe.Exit);
        Assert.Contains($"Probing {server.BaseUrl}/news/ for 2020-2020", probe.Out);
        Assert.Equal("20200101"u8.ToArray(), File.ReadAllBytes(PathOf("site/orphan_slugs.news.txt")));
        Assert.Equal(0, fetch.Exit);
        Assert.Contains("GET /news/20200101.php", server.Requests);
        Assert.DoesNotContain(server.Requests, q => q.Contains("//"));
        Assert.True(File.Exists(PathOf("site/raw/news/20200101.php.html")));
        Assert.True(File.Exists(PathOf("site/media/news/images/a.jpg")));
    }

    [Fact]
    public async Task Probe_MirroredDate_ItsSuffixOrphanIsFound_AndMirroredSuffixesAreNotRelisted()
    {
        // 盤點 §1.3 的實測:raw 只有 20210105 時,20210105A 原本找不到;已鏡像的 20210107A 原本照樣列出
        await using var server = new LoopbackHttpServer((_, path) =>
            path is "/ch/news/20210105.php" or "/ch/news/20210105A.php"
                or "/ch/news/20210107.php" or "/ch/news/20210107A.php"
                ? new LoopbackHttpServer.Response(200)
                : LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl);
        Write("site/raw/ch/news/20210105.php.html", "<html></html>");
        Write("site/raw/ch/news/20210107A.php.html", "<html></html>");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2021");

        Assert.Equal(0, r.Exit);
        Assert.Equal("20210105A\n20210107"u8.ToArray(), File.ReadAllBytes(PathOf("site/orphan_slugs.news.txt")));
    }

    [Fact]
    public async Task Probe_NoUsableAnswerAtAll_KeepsThePreviousSlugFile_AndExitsTwo()
    {
        // 全部請求都沒拿到有效回應(伺服器掛了、被 WAF 擋、base_url 打錯…):2.3.1 以前照樣把既有清單
        // 覆寫成空檔並 exit 0——空清單會被讀成「沒有孤兒頁」,先前探好的清單(預設延遲下一年約
        // 36 分鐘)也沒了。現在不覆寫,而且 exit 2(3.0,決策檔 H2 選項 B):只看 exit code 的排程也分得出
        // 「真的沒有」與「根本沒問到」
        await using var server = new LoopbackHttpServer((_, _) => new LoopbackHttpServer.Response(503));
        var config = SiteConfig(server.BaseUrl);
        var slugFile = Write("site/orphan_slugs.news.txt", "20190101\n20190102");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(2, r.Exit);
        Assert.Equal("20190101\n20190102", File.ReadAllText(slugFile));
        // 預設檔名帶 section(3.0),保留下來的就是這個 section 先前的清單;2.x 共用一個檔時要另外警告
        // 「可能是別的 section 的」,那段話已經不需要
        Assert.Equal(
            "error: none of the 732 probes got a usable answer (HTTP 503: 732)."
            + " Check site.base_url, site.user_agent, site.auth_workaround (cookies) and site.polite.delay_ms, then rerun.\n"
            + $"warning: {slugFile} was left as it was (it holds an earlier probe's list).\n",
            r.Err);
        Assert.DoesNotContain("Probe done", r.Out);   // 失敗了:stdout 不印「完成」
    }

    [Fact]
    public async Task Probe_DefaultOut_IsPerSection_SoSectionsDoNotOverwriteEachOther()
    {
        // 3.0(決策檔 H2 選項 E):預設 --out 帶 section。2.x 是各 section 共用的 orphan_slugs.txt:
        // 探完 events 會蓋掉 news 的清單,接著 fetch-orphans --section news 就拿 events 的 slug 去抓
        await using var server = new LoopbackHttpServer((_, path) =>
            path is "/ch/news/20200101.php" or "/ch/events/20200102.php"
                ? new LoopbackHttpServer.Response(200)
                : LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl);

        var news = await Run("probe-orphans", config, "--section", "news", "--years", "2020");
        var events = await Run("probe-orphans", config, "--section", "events", "--years", "2020");

        Assert.Equal(0, news.Exit);
        Assert.Equal(0, events.Exit);
        Assert.Equal("20200101", File.ReadAllText(PathOf("site/orphan_slugs.news.txt")));
        Assert.Equal("20200102", File.ReadAllText(PathOf("site/orphan_slugs.events.txt")));
        Assert.False(File.Exists(PathOf("site/orphan_slugs.txt")));
        Assert.Empty(news.Err);
    }

    /// <summary>
    /// 2.x 留下的共用清單:3.0 不讀、不寫、不刪,但每次用到預設檔名時都講一次怎麼遷移。
    /// 新檔還不存在時才叫人改名;新檔已經在了,改名會用舊清單蓋掉新清單,所以改叫人刪掉(審查建議 7)。
    /// </summary>
    private string LegacyRenameNote(string legacy, string section) =>
        $"note: {legacy} was written by PolyMigrate 2.x, which shared one slug list across every --section."
        + $" Since 3.0 the default list is per section ({PathOf($"site/orphan_slugs.{section}.txt")}) and the old file"
        + $" is no longer read or written. If it holds the {section} list, rename it to orphan_slugs.{section}.txt;"
        + " otherwise delete it.\n";

    private string LegacyDeleteNote(string legacy, string section) =>
        $"note: {legacy} was written by PolyMigrate 2.x, which shared one slug list across every --section."
        + $" Since 3.0 the default list is per section and {PathOf($"site/orphan_slugs.{section}.txt")} already exists;"
        + " the old file is no longer read or written, so delete it (renaming it would overwrite the newer list).\n";

    [Fact]
    public async Task Probe_Legacy2xSlugFile_IsLeftAlone_AndTheMigrationIsSpelledOut()
    {
        await using var server = new LoopbackHttpServer((_, path) => path == "/ch/news/20200101.php"
            ? new LoopbackHttpServer.Response(200)
            : LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl);
        var legacy = Write("site/orphan_slugs.txt", "20190101");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(0, r.Exit);
        Assert.Equal("20190101", File.ReadAllText(legacy));
        Assert.Equal("20200101", File.ReadAllText(PathOf("site/orphan_slugs.news.txt")));
        // 探完 orphan_slugs.news.txt 就有了:這時照「改名」去做,會用 2.x 的舊清單蓋掉剛探好的新清單
        Assert.Equal(LegacyDeleteNote(legacy, "news"), r.Err);
        Assert.True(r.Err.Replace(Root, "").All(char.IsAscii));
    }

    [Fact]
    public async Task Probe_Legacy2xSlugFile_NothingWritten_StillSaysRename()
    {
        // 一個回答都沒拿到:新檔沒寫出來,舊檔改名仍是對的遷移方式
        await using var server = new LoopbackHttpServer((_, _) => new LoopbackHttpServer.Response(503));
        var config = SiteConfig(server.BaseUrl);
        var legacy = Write("site/orphan_slugs.txt", "20190101");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(2, r.Exit);
        Assert.False(File.Exists(PathOf("site/orphan_slugs.news.txt")));
        Assert.EndsWith(LegacyRenameNote(legacy, "news"), r.Err);
    }

    [Fact]
    public async Task Probe_ExplicitOut_DoesNotMentionTheLegacyFile()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl);
        Write("site/orphan_slugs.txt", "20190101");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020", "--out", PathOf("site/list.txt"));

        Assert.Equal(0, r.Exit);
        Assert.Empty(r.Err);
    }

    [Theory]
    [InlineData("news/2020")]
    [InlineData("..")]
    public async Task DefaultSlugFile_NeedsASectionThatIsASingleName(string section)
    {
        // 預設檔名是 orphan_slugs.<section>.txt:section 不是單一名稱時拼不出一個檔名,要明講、請使用者指定
        var config = SiteConfig("https://example.invalid");

        var probe = await Run("probe-orphans", config, "--section", section, "--years", "2020");
        var fetch = await Run("fetch-orphans", config, "--section", section);

        Assert.Equal(2, probe.Exit);
        Assert.Equal($"--section '{section}' can't be part of the default slug file name; pass --out <file>.\n", probe.Err);
        Assert.Equal(2, fetch.Exit);
        Assert.Equal($"--section '{section}' can't be part of the default slug file name; pass --slugs <file>.\n", fetch.Err);
    }

    [Fact]
    public async Task Probe_NoUsableAnswerAtAll_DoesNotCreateAnEmptySlugFile()
    {
        // 沒有舊清單時也不建空檔:空檔正是「沒有孤兒頁」的假象;沒有檔的話 fetch-orphans 會明說找不到清單
        await using var server = new LoopbackHttpServer((_, _) => new LoopbackHttpServer.Response(403));
        var config = SiteConfig(server.BaseUrl);

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(2, r.Exit);
        Assert.False(File.Exists(PathOf("site/orphan_slugs.news.txt")));
        Assert.StartsWith("error: none of the 732 probes got a usable answer (HTTP 403: 732)", r.Err);
        Assert.EndsWith($"warning: {PathOf("site/orphan_slugs.news.txt")} was not written; an empty list would read as \"no orphan pages\".\n", r.Err);
        // 先拿掉暫存路徑再檢查:TEMP 含非 ASCII 的開發機(例如中文使用者名稱)上,路徑本身就不是 ASCII
        Assert.True(r.Err.Replace(Root, "").All(char.IsAscii), "警告要是純 ASCII:cp437 等 console 印不出 × 與 —");
    }

    [Fact]
    public async Task Probe_SomeProbesUnanswered_WritesTheHits_WarnsTheListMayBeIncomplete_AndExitsOne()
    {
        // 部分沒回答(例如 12 月整個月被限流):命中的照寫,但要講明清單可能不完整。
        // exit 1(3.0 起;2.x 印了 warning 卻回 0):契約的「1 = 有 warning、輸出仍可用」正是這個情況
        await using var server = new LoopbackHttpServer((_, path) => path switch
        {
            "/ch/news/20200101.php" => new LoopbackHttpServer.Response(200),
            _ when path.StartsWith("/ch/news/12") || path.StartsWith("/ch/news/202012") => new LoopbackHttpServer.Response(429),
            _ => LoopbackHttpServer.Response.NotFound,
        });
        var config = SiteConfig(server.BaseUrl);

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(1, r.Exit);
        Assert.Equal("20200101"u8.ToArray(), File.ReadAllBytes(PathOf("site/orphan_slugs.news.txt")));
        // 12 月 31 天 × 2 種格式 = 62 個沒回答;20200101 命中後的 A 探測(404)也算一次
        Assert.Equal(
            "warning: 62 of 733 probes got no usable answer (HTTP 429: 62); the list may be incomplete."
            + " Rerun later to fill the gaps.\n",
            r.Err);
    }

    [Fact]
    public async Task Probe_RedirectIsNotAHit()
    {
        // 舊站常把不存在的網址一律轉址到首頁(soft 404)。probe 若跟著轉址,首頁的 200 會讓
        // 每一個日期候選都「命中」——所以 probe 的 HttpClient 刻意不跟轉址(fetch 才跟,見下面)
        await using var server = new LoopbackHttpServer((_, path) => path switch
        {
            "/ch/news/20200101.php" => LoopbackHttpServer.Response.Redirect("/ch/index.php"),
            "/ch/news/20200102.php" or "/ch/index.php" => new LoopbackHttpServer.Response(200),
            _ => LoopbackHttpServer.Response.NotFound,
        });
        var config = SiteConfig(server.BaseUrl);

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(0, r.Exit);
        Assert.Equal("20200102"u8.ToArray(), File.ReadAllBytes(PathOf("site/orphan_slugs.news.txt")));
        Assert.DoesNotContain("HEAD /ch/index.php", server.Requests);   // 沒有跟過去
        Assert.Empty(r.Err);   // 轉址與 404 都是「確定沒有」的回答,不是「沒拿到回答」
    }

    [Fact]
    public async Task Probe_SlugFileCannotBeWritten_ExitsTwo()
    {
        // 寫不出 slug 檔是 IO 錯誤:exit 2 + 「error: 訊息」,不是崩潰也不是 0。
        // --out 指到一個既有目錄——檔案寫不進目錄,三個平台都一樣(丟的例外型別不同,都要接住)
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl);
        var blocker = PathOf("lists");
        Directory.CreateDirectory(blocker);

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020", "--out", blocker);

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("error: ", r.Err);
        Assert.Empty(Directory.GetFileSystemEntries(blocker));
        // 而且在探測之前就知道:原本要先發完 732 個請求(預設延遲下一年約 36 分鐘)才在寫檔時失敗
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Probe_OutDirectoryMissing_ExitsTwo_BeforeAnyRequest()
    {
        await using var server = new LoopbackHttpServer((_, _) => LoopbackHttpServer.Response.NotFound);
        var config = SiteConfig(server.BaseUrl);

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020",
            "--out", PathOf("no-such-dir/slugs.txt"));

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("error: ", r.Err);
        Assert.Empty(server.Requests);
        Assert.False(Directory.Exists(PathOf("no-such-dir")));
    }

    [Fact]
    public async Task Probe_WritabilityCheck_LeavesNoTraceWhenNothingIsWritten()
    {
        // 探測前的可寫檢查不可以留下空檔(那正是「沒有孤兒頁」的假象),也不可以動到既有檔
        await using var server = new LoopbackHttpServer((_, _) => new LoopbackHttpServer.Response(503));
        var config = SiteConfig(server.BaseUrl);
        var existing = Write("site/existing.txt", "20190101");
        var before = File.GetLastWriteTimeUtc(existing);

        var fresh = await Run("probe-orphans", config, "--section", "news", "--years", "2020");
        var kept = await Run("probe-orphans", config, "--section", "news", "--years", "2020", "--out", existing);

        Assert.Equal(2, fresh.Exit);   // 一個回答都沒拿到 = error(3.0)
        Assert.False(File.Exists(PathOf("site/orphan_slugs.news.txt")));
        Assert.Equal(2, kept.Exit);
        Assert.Equal("20190101", File.ReadAllText(existing));
        Assert.Equal(before, File.GetLastWriteTimeUtc(existing));
    }

    [Fact]
    public async Task Probe_YearZero_ExitsTwo_WithoutWritingTheSlugFile()
    {
        var config = SiteConfig("https://example.invalid");

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "0");

        Assert.Equal(2, r.Exit);
        Assert.False(File.Exists(PathOf("site/orphan_slugs.news.txt")));
    }

    [Fact]
    public async Task Probe_MissingSection_ExitsTwo_WithUsage()
    {
        var r = await Run("probe-orphans", "site.yaml", "--years", "2020");

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("Usage: polymigrate probe-orphans", r.Err);
    }

    [Fact]
    public async Task Probe_InvalidConfig_ExitsTwo()
    {
        var config = Write("site/site.yaml", Config(baseUrl: "ftp://example.invalid"));

        var r = await Run("probe-orphans", config, "--section", "news", "--years", "2020");

        Assert.Equal(2, r.Exit);
        Assert.Contains("site.base_url must be an absolute http(s) URL", r.Err);
    }

    // ── fetch-orphans ───────────────────────────────────────────────────────

    private const string OrphanPage =
        "<html><body><main><p>孤兒頁</p><img src=\"images/a.jpg\"></main></body></html>";

    [Fact]
    public async Task Fetch_AllPagesFetched_ExitsZero_BlankSlugLinesSkipped()
    {
        await using var server = new LoopbackHttpServer((_, path) => path.EndsWith(".jpg")
            ? new LoopbackHttpServer.Response(200, "image/jpeg", [1, 2, 3])
            : LoopbackHttpServer.Response.Html(OrphanPage));
        var config = SiteConfig(server.BaseUrl);
        // 人工編過的清單:前後空白、空行、只有空白的行(CLI 逐行 Trim、略過空行)
        Write("site/orphan_slugs.news.txt", "  20200101  \n\n   \n20200102\n");

        var r = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(0, r.Exit);
        Assert.Contains("Fetching 2 orphan slugs × 2 langs...\n", r.Out);
        Assert.Contains("  pages fetched : 4\n", r.Out);
        Assert.Contains("  assets fetched: 2\n", r.Out);   // ch/en 各一張(路徑不同)
        Assert.Equal(Encoding.UTF8.GetBytes(OrphanPage), File.ReadAllBytes(PathOf("site/raw/en/news/20200102.php.html")));
        Assert.True(File.Exists(PathOf("site/media/ch/news/images/a.jpg")));
        // 空行不得變成 /ch/news/.php 之類的請求
        Assert.Equal(6, server.Requests.Count);
        Assert.DoesNotContain(server.Requests, q => q.Contains("/news/.php"));
    }

    [Fact]
    public async Task Fetch_SomePagesMissing_ExitsOne_ListingThem()
    {
        await using var server = new LoopbackHttpServer((_, path) => path == "/en/news/20200101.php"
            ? LoopbackHttpServer.Response.NotFound
            : LoopbackHttpServer.Response.Html("<html><body><main>x</main></body></html>"));
        var config = SiteConfig(server.BaseUrl);
        var slugs = Write("lists/news.txt", "20200101");

        var r = await Run("fetch-orphans", config, "--section", "news", "--slugs", slugs);

        Assert.Equal(1, r.Exit);
        Assert.Contains("  pages fetched : 1\n", r.Out);
        Assert.Contains("  [404] en/news/20200101\n", r.Out);
    }

    [Fact]
    public async Task Fetch_AlreadyMirrored_IsSkipped_NotRefetched()
    {
        await using var server = new LoopbackHttpServer((_, _) =>
            LoopbackHttpServer.Response.Html("<html><body><main>new</main></body></html>"));
        var config = SiteConfig(server.BaseUrl);
        var existing = Write("site/raw/ch/news/20200101.php.html", "mirrored earlier");
        Write("site/orphan_slugs.news.txt", "20200101");

        var r = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(0, r.Exit);
        Assert.Contains("  pages skipped : 1 (already mirrored)\n", r.Out);
        Assert.Equal("mirrored earlier", File.ReadAllText(existing));   // raw/ 不可再生:只寫不覆寫
        Assert.Equal(["GET /en/news/20200101.php"], server.Requests);
    }

    [Fact]
    public async Task Fetch_FollowsRedirects_SavingUnderTheRequestedSlug()
    {
        // 與 probe 相反:fetch 要跟轉址(頁面搬家後舊網址 301 過去),存檔名仍是要求的那個 slug
        const string page = "<html><body><main>搬過家的頁</main></body></html>";
        await using var server = new LoopbackHttpServer((_, path) => path switch
        {
            "/ch/news/20200101.php" => LoopbackHttpServer.Response.Redirect("/ch/news/moved.php", 301),
            "/ch/news/moved.php" => LoopbackHttpServer.Response.Html(page),
            _ => LoopbackHttpServer.Response.NotFound,
        });
        var config = SiteConfig(server.BaseUrl, langMap: "{ ch: zh-Hant }");
        Write("site/orphan_slugs.news.txt", "20200101");

        var r = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(0, r.Exit);
        Assert.Contains("  pages fetched : 1\n", r.Out);
        Assert.Equal(Encoding.UTF8.GetBytes(page), File.ReadAllBytes(PathOf("site/raw/ch/news/20200101.php.html")));
        Assert.Equal(["GET /ch/news/20200101.php", "GET /ch/news/moved.php"], server.Requests);
    }

    [Fact]
    public async Task Fetch_RootOverride_ReadsTheSlugListAndWritesUnderThatRoot()
    {
        // --root 決定三件事:預設的 slug 清單、raw/ 與 media/ 寫到哪——config 所在目錄一樣都不碰
        await using var server = new LoopbackHttpServer((_, path) => path.EndsWith(".jpg")
            ? new LoopbackHttpServer.Response(200, "image/jpeg", [1, 2, 3])
            : LoopbackHttpServer.Response.Html(OrphanPage));
        var config = Write("cfg/site.yaml", Config(baseUrl: server.BaseUrl));
        Write("work/orphan_slugs.news.txt", "20200101");

        var r = await Run("fetch-orphans", config, "--section", "news", "--root", PathOf("work"));

        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(PathOf("work/raw/ch/news/20200101.php.html")));
        Assert.True(File.Exists(PathOf("work/media/ch/news/images/a.jpg")));
        Assert.Equal(["site.yaml"], Directory.GetFileSystemEntries(PathOf("cfg")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Fetch_InvalidContentSelector_ExitsTwo_LeavingThePageForTheRerun()
    {
        // 原本:頁面先寫進 raw/,掃資產時 DomException 崩潰(exit code 不在契約內);修好 config 重跑,
        // 這頁因「已存在」被跳過,資產永遠補不回來。現在 exit 2,而且這頁沒寫,重跑會連資產一起抓
        await using var server = new LoopbackHttpServer((_, path) => path.EndsWith(".jpg")
            ? new LoopbackHttpServer.Response(200, "image/jpeg", [1, 2, 3])
            : LoopbackHttpServer.Response.Html(OrphanPage));
        var good = Config(baseUrl: server.BaseUrl, langMap: "{ ch: zh-Hant }");
        var config = Write("site/site.yaml", good.Replace("content: \"main\"", "content: \"main[\""));
        Write("site/orphan_slugs.news.txt", "20200101");

        var r = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(2, r.Exit);
        Assert.Equal($"{config}: config is invalid:\n  - extract.content: 'main[' is not a valid CSS selector.\n", r.Err);   // 3.0 起載入時就擋
        Assert.Empty(server.Requests);   // 一個請求都沒發
        Assert.False(File.Exists(PathOf("site/raw/ch/news/20200101.php.html")));

        Write("site/site.yaml", good);
        var rerun = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(0, rerun.Exit);
        Assert.True(File.Exists(PathOf("site/raw/ch/news/20200101.php.html")));
        Assert.True(File.Exists(PathOf("site/media/ch/news/images/a.jpg")));
    }

    [Fact]
    public async Task Fetch_CancelledMidway_Returns130()
    {
        using var cts = new CancellationTokenSource();
        await using var server = new LoopbackHttpServer((_, _) =>
        {
            cts.Cancel();
            return LoopbackHttpServer.Response.Html("<html><body><main>x</main></body></html>");
        });
        var config = SiteConfig(server.BaseUrl);
        Write("site/orphan_slugs.news.txt", "20200101\n20200102");

        var r = await RunWith(cts.Token, "fetch-orphans", config, "--section", "news");

        Assert.Equal(130, r.Exit);
        Assert.Equal("cancelled.\n", r.Err);
        Assert.False(Directory.Exists(PathOf("site/raw")));   // 第一個請求就被中斷:什麼都沒寫
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Fetch_AlreadyCancelled_Returns130_WithoutAnyRequest()
    {
        var config = SiteConfig("https://example.invalid");
        Write("site/orphan_slugs.news.txt", "20200101");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var r = await RunWith(cts.Token, "fetch-orphans", config, "--section", "news");

        Assert.Equal(130, r.Exit);
        Assert.Equal("cancelled.\n", r.Err);
    }

    [Fact]
    public async Task Fetch_SlugListMissing_ExitsTwo()
    {
        var config = SiteConfig("https://example.invalid");

        var r = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(2, r.Exit);
        Assert.Equal($"Slug list not found: {PathOf("site/orphan_slugs.news.txt")} (run probe-orphans first)\n", r.Err);
    }

    [Fact]
    public async Task Fetch_OnlyTheLegacy2xSlugFile_ExitsTwo_AndSaysHowToMigrate()
    {
        // 2.x 的 orphan_slugs.txt 不再自動讀(它可能是任何一個 section 的清單),但不可以只說「找不到」:
        // 使用者手上明明有一份清單。講明它是什麼、怎麼改名,或用 --slugs 直接指定
        var config = SiteConfig("https://example.invalid");
        var legacy = Write("site/orphan_slugs.txt", "20200101");

        var r = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(2, r.Exit);
        Assert.Equal(
            $"Slug list not found: {PathOf("site/orphan_slugs.news.txt")}.\n"
            + $"{legacy} was written by PolyMigrate 2.x, which shared one slug list across every --section;"
            + " 3.0 no longer reads it on its own. If it holds the news list, rename it to orphan_slugs.news.txt,"
            + $" or pass --slugs {legacy}.\n",
            r.Err);
    }

    [Fact]
    public async Task Fetch_BothFiles_ReadsThePerSectionList_AndNotesTheLegacyFile()
    {
        await using var server = new LoopbackHttpServer((_, _) =>
            LoopbackHttpServer.Response.Html("<html><body><main>x</main></body></html>"));
        var config = SiteConfig(server.BaseUrl, langMap: "{ ch: zh-Hant }");
        var legacy = Write("site/orphan_slugs.txt", "20200101");
        Write("site/orphan_slugs.news.txt", "20200102");

        var r = await Run("fetch-orphans", config, "--section", "news");

        Assert.Equal(0, r.Exit);
        Assert.Equal(["GET /ch/news/20200102.php"], server.Requests);
        Assert.Equal(LegacyDeleteNote(legacy, "news"), r.Err);   // 兩個檔都在:新檔才是讀的那一份
    }

    [Fact]
    public async Task Fetch_MissingSection_ExitsTwo_WithUsage()
    {
        var r = await Run("fetch-orphans", "site.yaml");

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("Usage: polymigrate fetch-orphans", r.Err);
    }

    [Theory]
    [InlineData("--section")]
    [InlineData("--slugs")]
    [InlineData("--root")]
    public async Task Fetch_OptionWithoutValue_ExitsTwo(string option)
    {
        var r = await Run("fetch-orphans", "site.yaml", option);

        Assert.Equal(2, r.Exit);
        Assert.StartsWith($"Option {option} requires a value.\nUsage: polymigrate fetch-orphans", r.Err);
    }
}
