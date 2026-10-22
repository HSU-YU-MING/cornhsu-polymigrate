using System.Net;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Orphans;

namespace PolyMigrate.Core.Tests.Orphans;

/// <summary>
/// <see cref="PolyMigrator.ProbeOrphansAsync(string, string, int, int, string?, Action{string}?, CancellationToken)"/> 與
/// <see cref="PolyMigrator.FetchOrphansAsync(string, string, IReadOnlyList{string}, Action{string}?, CancellationToken)"/>
/// (3.0,決策檔 L6 選項 B)。2.x 的函式庫呼叫端要自己組 OrphanProber / OrphanFetcher、自己算已知 slug,
/// 而且拿不到「一個回答都沒拿到」的統計。這裡走 internal 的 HttpClient 注入多載,不打真站;
/// 規則要與 CLI 的 probe-orphans / fetch-orphans 一致。
/// </summary>
public class OrphanFacadeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-orphan-facade").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static PolyMigrator Migrator()
    {
        var config = TestConfigs.IbpsLike();
        config.Site.Polite = new PoliteSection { DelayMs = 0 };
        return new PolyMigrator(config);
    }

    private void Mirror(string relative)
    {
        var path = Path.Combine(_root, "raw", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<html></html>");
    }

    [Fact]
    public async Task ProbeOrphans_SkipsMirroredSlugs_ByTheConfigsRule_AndDefaultsToTheFirstLang()
    {
        // 與 CLI 同一套:語言預設 lang_map 第一個 key(ch);已知 slug 取自 root/raw,依 strip_extensions 判定
        Mirror("ch/news/20210101.php.html");
        Mirror("ch/news/20210103.PHP.html");   // 大小寫與 strip_extensions 不符:extract 的 slug 是 20210103.PHP,不算已知
        var handler = new StubHandler(req => StubHandler.Status(
            req.RequestUri!.AbsolutePath == "/ch/news/20210102.php" ? HttpStatusCode.OK : HttpStatusCode.NotFound));

        var report = await Migrator().ProbeOrphansAsync(new HttpClient(handler), _root, "news", 2021, 2021, null, null, default);

        Assert.Equal(["20210102"], report.Found);
        Assert.False(report.NoUsableAnswers);
        Assert.Equal(0, report.UnansweredCount);
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/ch/news/20210101.php"));   // 已鏡像:本身不探
        Assert.Contains(handler.Requests, r => r.EndsWith("/ch/news/20210101A.php"));        // 後綴照探
        Assert.Contains(handler.Requests, r => r.EndsWith("/ch/news/20210103.php"));
        Assert.All(handler.Requests, r => Assert.StartsWith("HEAD https://www.ibps-austin.org/ch/news/", r));
        Assert.Empty(Directory.GetFiles(_root));   // facade 不寫清單檔:寫不寫、寫到哪由呼叫端決定
    }

    [Fact]
    public async Task ProbeOrphans_NoUsableAnswers_IsReported_NotJustAnEmptyList()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        var report = await Migrator().ProbeOrphansAsync(new HttpClient(handler), _root, "news", 2021, 2021, "en", null, default);

        Assert.Empty(report.Found);
        Assert.True(report.NoUsableAnswers);   // CLI 在這個情況 exit 2、不寫清單
        Assert.Equal(730, report.Checks);
        Assert.Equal(new Dictionary<string, int> { ["HTTP 503"] = 730 }, report.Unanswered);
        Assert.All(handler.Requests, r => Assert.Contains("/en/news/", r));
    }

    [Theory]
    [InlineData(0, 2021)]
    [InlineData(2022, 2021)]
    [InlineData(2021, 10000)]
    public async Task ProbeOrphans_InvalidYears_Throw_BeforeAnyRequest(int yearFrom, int yearTo)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Migrator().ProbeOrphansAsync(_root, "news", yearFrom, yearTo));
    }

    [Fact]
    public async Task FetchOrphans_EveryLang_IntoRootRawAndMedia()
    {
        const string page = "<html><body><section id=\"main\"><p>x</p><img src=\"images/a.jpg\"></section></body></html>";
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith(".jpg")
            ? StubHandler.Bytes([1, 2, 3])
            : StubHandler.Html(page));
        Mirror("en/news/20210101.php.html");   // 已鏡像:跳過(只寫不刪)

        var report = await Migrator().FetchOrphansAsync(new HttpClient(handler), _root, "news", ["20210101"], null, default);

        Assert.Equal(1, report.PagesFetched);
        Assert.Equal(1, report.PagesSkipped);
        Assert.Equal(1, report.AssetsFetched);
        Assert.Empty(report.Errors);
        Assert.True(File.Exists(Path.Combine(_root, "raw", "ch", "news", "20210101.php.html")));
        Assert.True(File.Exists(Path.Combine(_root, "media", "ch", "news", "images", "a.jpg")));
    }

    [Fact]
    public async Task ProbeAndFetch_AreCancellable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.NotFound));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Migrator().ProbeOrphansAsync(new HttpClient(handler), _root, "news", 2021, 2021, null, null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Migrator().FetchOrphansAsync(new HttpClient(handler), _root, "news", ["20210101"], null, cts.Token));
        Assert.Empty(handler.Requests);
    }
}
