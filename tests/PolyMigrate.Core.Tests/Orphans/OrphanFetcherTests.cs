using System.Net;
using System.Text;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Orphans;

namespace PolyMigrate.Core.Tests.Orphans;

public class OrphanFetcherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-fetch").FullName;
    private string RawDir => Path.Combine(_root, "raw");
    private string MediaDir => Path.Combine(_root, "media");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SiteConfig FastConfig()
    {
        var config = TestConfigs.IbpsLike();
        config.Site.Polite = new PoliteSection { DelayMs = 0 };
        return config;
    }

    private const string PageHtml =
        """
        <html><body>
        <section id="header"><img src="/frame/logo.jpg"></section>
        <section id="main"><img src="images/20210505_1.jpg">
        <img src="https://cdn.other.org/x.jpg"></section>
        </body></html>
        """;

    private Task<FetchReport> Fetch(StubHandler handler, params string[] slugs) =>
        new OrphanFetcher(FastConfig(), new HttpClient(handler))
            .FetchAsync(slugs, "news", RawDir, MediaDir);

    [Fact]
    public async Task FetchesAllLangs_SavesRawBytes_AndBodyAssetsOnly()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/ch/news/20210505.php" or "/en/news/20210505.php" => StubHandler.Html(PageHtml),
            "/ch/news/images/20210505_1.jpg" or "/en/news/images/20210505_1.jpg"
                => StubHandler.Bytes([1, 2, 3]),
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        });

        var report = await Fetch(handler, "20210505");

        Assert.Equal(2, report.PagesFetched);          // ch + en
        Assert.Equal(2, report.AssetsFetched);
        Assert.True(File.Exists(Path.Combine(RawDir, "ch", "news", "20210505.php.html")));
        Assert.True(File.Exists(Path.Combine(MediaDir, "ch", "news", "images", "20210505_1.jpg")));
        // 框架區的圖與外部主機的圖都不抓(規格:只收正文 section 的資產)
        Assert.DoesNotContain(handler.Requests, r => r.Contains("/frame/logo.jpg"));
        Assert.DoesNotContain(handler.Requests, r => r.Contains("cdn.other.org"));
    }

    [Fact]
    public async Task MissingLangVersion_RecordedAsError_OthersProceed()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/ch/news/20210505.php" => StubHandler.Html("<html><body><section id=\"main\">x</section></body></html>"),
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        });

        var report = await Fetch(handler, "20210505");

        Assert.Equal(1, report.PagesFetched);
        Assert.Contains(report.Errors, e => e.Contains("[404] en/news/20210505"));
    }

    [Fact]
    public async Task ExistingRawFile_Skipped()
    {
        var existing = Path.Combine(RawDir, "ch", "news", "20210505.php.html");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "cached");
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/en/")
            ? StubHandler.Html("<html><body><section id=\"main\">x</section></body></html>")
            : StubHandler.Status(HttpStatusCode.NotFound));

        var report = await Fetch(handler, "20210505");

        Assert.Equal(1, report.PagesSkipped);
        Assert.Equal("cached", File.ReadAllText(existing));   // 不覆寫既有鏡像
        Assert.DoesNotContain(handler.Requests, r => r.Contains("/ch/news/20210505.php"));
    }

    [Fact]
    public async Task RequestTimeout_RecordedAndDoesNotAbortBatch()
    {
        // HttpClient.Timeout 到期丟的是 TaskCanceledException(非 HttpRequestException),
        // 且呼叫端 ct 未取消 → 視為暫時性失敗:記錄該項、其餘照抓,不可掀掉整批
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/ch/news/20210505.php" => throw new TaskCanceledException("timeout"),
            "/en/news/20250606.php" => StubHandler.Html("<html><body><section id=\"main\">x</section></body></html>"),
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        });

        var report = await Fetch(handler, "20210505", "20250606");

        Assert.Equal(1, report.PagesFetched);   // 20250606/en 仍成功抓到
        Assert.Contains(report.Errors, e => e.Contains("20210505") && e.Contains("timeout"));
    }

    private const string MinimalPage = "<html><body><section id=\"main\">x</section></body></html>";

    [Fact]
    public async Task ErrorEntries_UseTheDocumentedFormat()
    {
        // CLI 會把 Errors 原樣印出、每行一筆;使用者靠 [狀態碼] lang/section/slug 去找是哪一頁
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/ch/news/20210505.php" => StubHandler.Bytes([1, 2, 3]),   // 200 但不是 HTML(例如被導到圖片)
            "/en/news/20210505.php" => StubHandler.Status(HttpStatusCode.Forbidden),
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        });

        var report = await Fetch(handler, "20210505");

        Assert.Equal(["[200] ch/news/20210505", "[403] en/news/20210505"], report.Errors);
        Assert.Equal(0, report.PagesFetched);
        Assert.False(Directory.Exists(RawDir));   // 不是 HTML 的回應不得當成頁面存檔
    }

    [Fact]
    public async Task OkWithoutContentType_IsNotSavedAsAPage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<html></html>")),
        });

        var report = await Fetch(handler, "20210505");

        Assert.Equal(["[200] ch/news/20210505", "[200] en/news/20210505"], report.Errors);
        Assert.False(Directory.Exists(RawDir));
    }

    [Fact]
    public async Task ConnectionError_RecordedWithUrlAndMessage_OthersProceed()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/ch/news/20210505.php" => throw new HttpRequestException("connection reset"),
            _ => StubHandler.Html(MinimalPage),
        });

        var report = await Fetch(handler, "20210505");

        Assert.Equal(["[err] https://www.ibps-austin.org/ch/news/20210505.php connection reset"], report.Errors);
        Assert.Equal(1, report.PagesFetched);
    }

    [Fact]
    public async Task Progress_ReportsEachFetchedPage()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/ch/")
            ? StubHandler.Html(MinimalPage)
            : StubHandler.Status(HttpStatusCode.NotFound));
        var messages = new List<string>();

        await new OrphanFetcher(FastConfig(), new HttpClient(handler))
            .FetchAsync(["a", "b"], "news", RawDir, MediaDir, messages.Add);

        Assert.Equal(["fetched ch/news/a", "fetched ch/news/b"], messages);
    }

    [Fact]
    public async Task Assets_OnlySameHostKnownExtensions_DecodedOnDisk_FailuresIgnored()
    {
        const string html =
            """
            <html><body><section id="main">
            <img src="images/a%20b.jpg">
            <img src="images/missing.png">
            <img src="images/empty.gif">
            <img src="images/slow.webp">
            <img src="images/script.js">
            <img src="http://[broken">
            <video><source src="/video/clip.mp4"></video>
            </section></body></html>
            """;
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/ch/news/20210505.php" => StubHandler.Html(html),
            "/ch/news/images/a%20b.jpg" => StubHandler.Bytes([1]),
            "/ch/news/images/empty.gif" => StubHandler.Bytes([]),
            "/ch/news/images/slow.webp" => throw new TaskCanceledException("timeout"),
            "/video/clip.mp4" => StubHandler.Bytes([2]),
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        });
        var config = FastConfig();
        config.UrlPattern.LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant" };

        var report = await new OrphanFetcher(config, new HttpClient(handler))
            .FetchAsync(["20210505"], "news", RawDir, MediaDir);

        // 資產失敗(404、0 byte、逾時)不阻斷、也不算頁面錯誤:那多半就是原站壞圖
        Assert.Empty(report.Errors);
        Assert.Equal(1, report.PagesFetched);
        Assert.Equal(2, report.AssetsFetched);
        // §2.6:URL 單次編碼,磁碟存解碼後的檔名
        Assert.True(File.Exists(Path.Combine(MediaDir, "ch", "news", "images", "a b.jpg")));
        Assert.True(File.Exists(Path.Combine(MediaDir, "video", "clip.mp4")));
        Assert.False(File.Exists(Path.Combine(MediaDir, "ch", "news", "images", "empty.gif")));
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith(".js"));
        Assert.Empty(Directory.GetFiles(MediaDir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingAsset_NotRequestedAgain()
    {
        var existing = Path.Combine(MediaDir, "ch", "news", "images", "20210505_1.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "already here");
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath == "/ch/news/20210505.php"
            ? StubHandler.Html(PageHtml)
            : StubHandler.Bytes([9]));
        var config = FastConfig();
        config.UrlPattern.LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant" };

        var report = await new OrphanFetcher(config, new HttpClient(handler))
            .FetchAsync(["20210505"], "news", RawDir, MediaDir);

        Assert.Equal(0, report.AssetsFetched);
        Assert.Equal("already here", File.ReadAllText(existing));   // 只寫不覆寫:media/ 不可再生
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("20210505_1.jpg"));
    }

    [Fact]
    public async Task UserCancellation_DuringTheLastRequest_IsNotSwallowedAsATimeout()
    {
        // 與上面的逾時測試是一體兩面:HttpClient 的逾時與使用者 Ctrl-C 丟的都是
        // TaskCanceledException,只能靠「呼叫端的 token 有沒有被取消」分辨。
        // 把取消當成逾時吞掉的話,最後一個請求被中斷時整批會「正常結束」——CLI 回 1 而不是 130。
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.StartsWith("/en/"))
            {
                cts.Cancel();
                throw new TaskCanceledException("cancelled by user");
            }
            return StubHandler.Html(MinimalPage);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OrphanFetcher(FastConfig(), new HttpClient(handler))
                .FetchAsync(["20210505"], "news", RawDir, MediaDir, ct: cts.Token));
    }

    [Fact]
    public async Task AlreadyCancelled_MakesNoRequests()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new StubHandler(_ => StubHandler.Html(MinimalPage));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OrphanFetcher(FastConfig(), new HttpClient(handler))
                .FetchAsync(["20210505"], "news", RawDir, MediaDir, ct: cts.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FailedWrite_LeavesNoTmpFileBehind()
    {
        // 原子寫入:先寫 .tmp 再改名;改名失敗時要把 .tmp 清掉,不留垃圾在 raw/ 裡。
        // 製造改名失敗:目標路徑已被一個「目錄」佔住(File.Exists 為 false,所以會去抓)
        var target = Path.Combine(RawDir, "ch", "news", "20210505.php.html");
        Directory.CreateDirectory(target);
        var handler = new StubHandler(_ => StubHandler.Html(MinimalPage));
        var config = FastConfig();
        config.UrlPattern.LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant" };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new OrphanFetcher(config, new HttpClient(handler)).FetchAsync(["20210505"], "news", RawDir, MediaDir));

        Assert.Empty(Directory.GetFiles(Path.Combine(RawDir, "ch", "news"), "*.tmp"));
    }

    [Fact]
    public async Task RawBytes_PreservedVerbatim()
    {
        // 與 Python 版的刻意差異:存原始 bytes,編碼交給 config(§3.1)
        var content = "<html><body><section id=\"main\">中文內容</section></body></html>";
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith(".php")
            ? StubHandler.Html(content)
            : StubHandler.Status(HttpStatusCode.NotFound));

        await Fetch(handler, "20210505");

        var saved = File.ReadAllBytes(Path.Combine(RawDir, "ch", "news", "20210505.php.html"));
        Assert.Equal(Encoding.UTF8.GetBytes(content), saved);
    }
}
