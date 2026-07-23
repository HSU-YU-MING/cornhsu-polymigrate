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
