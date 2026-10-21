using System.Net;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Orphans;

namespace PolyMigrate.Core.Tests.Orphans;

public class OrphanProberTests
{
    private static SiteConfig FastConfig()
    {
        var config = TestConfigs.IbpsLike();
        config.Site.Polite = new PoliteSection { DelayMs = 0 };
        return config;
    }

    private static OrphanProber Prober(StubHandler handler) =>
        new(FastConfig(), new HttpClient(handler) { BaseAddress = null });

    [Fact]
    public void DateCandidates_BothFormatsPerDay()
    {
        var year2021 = OrphanProber.DateCandidates(2021, 2021).ToList();

        Assert.Equal(365 * 2, year2021.Count);
        Assert.Equal("01012021", year2021[0]);    // 每天先 MMDDYYYY
        Assert.Equal("20210101", year2021[1]);    // 再 YYYYMMDD
        Assert.Contains("02282021", year2021);
        Assert.DoesNotContain("02292021", year2021);   // 非閏年
        Assert.Contains("02292024", OrphanProber.DateCandidates(2024, 2024));   // 閏年
    }

    [Fact]
    public async Task Hit_ProbesSuffixChain_UntilFirstMiss()
    {
        // 規格:命中再探 A/B/C/D,「A 沒有就不會有 B」
        string[] existing = ["20210505", "20210505A", "20210505B"];
        var handler = new StubHandler(req =>
            StubHandler.Status(existing.Any(s => req.RequestUri!.AbsolutePath.EndsWith($"/{s}.php"))
                ? HttpStatusCode.OK : HttpStatusCode.NotFound));

        var found = await Prober(handler).ProbeAsync("ch", "news", 2021, 2021, new HashSet<string>());

        Assert.Equal(["20210505", "20210505A", "20210505B"], found);
        // C 探過(miss 後停),D 不該再探
        Assert.Contains(handler.Requests, r => r.EndsWith("20210505C.php"));
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("20210505D.php"));
    }

    [Fact]
    public async Task Conflict409_RetriedOnce_ThenSucceeds()
    {
        // §2.6:bot 防護回 409 → 退避重試
        var attempts = 0;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/01012021.php"))
            {
                return StubHandler.Status(++attempts == 1 ? HttpStatusCode.Conflict : HttpStatusCode.OK);
            }
            return StubHandler.Status(HttpStatusCode.NotFound);
        });

        var found = await Prober(handler).ProbeAsync("ch", "news", 2021, 2021, new HashSet<string>());

        Assert.Contains("01012021", found);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task KnownSlugs_NotProbed()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.NotFound));

        await Prober(handler).ProbeAsync("ch", "news", 2021, 2021,
            new HashSet<string> { "01012021", "20210101" });

        Assert.DoesNotContain(handler.Requests, r => r.Contains("01012021") || r.Contains("20210101"));
    }

    [Fact]
    public async Task Probe_UsesHeadRequests()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.NotFound));

        await Prober(handler).ProbeAsync("ch", "news", 2021, 2021, new HashSet<string>());

        Assert.All(handler.Requests, r => Assert.StartsWith("HEAD ", r));
    }

    [Fact]
    public async Task Conflict409Twice_GivesUpAfterOneRetry()
    {
        // 退避只重試一次:一直 409 的候選視為不存在,不可無限重試卡住整批
        var attempts = 0;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/01012021.php"))
            {
                attempts++;
                return StubHandler.Status(HttpStatusCode.Conflict);
            }
            return StubHandler.Status(HttpStatusCode.NotFound);
        });

        var found = await Prober(handler).ProbeAsync("ch", "news", 2021, 2021, new HashSet<string>());

        Assert.DoesNotContain("01012021", found);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]   // HttpClient.Timeout 到期:TaskCanceledException,呼叫端 token 沒取消
    [InlineData(true)]    // 連線層錯誤:HttpRequestException
    public async Task TransientFailure_RetriedOnce_ThenTreatedAsMiss_BatchContinues(bool connectionError)
    {
        // CLAUDE.md「已知教訓」:逾時丟的是 TaskCanceledException 不是 HttpRequestException,
        // 原本會讓 probe 跑到一半整批中止。fetch 那邊有測,probe 這邊同一條規則也要釘住。
        var attempts = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/01012021.php"))
            {
                attempts++;
                throw connectionError ? new HttpRequestException("reset") : new TaskCanceledException("timeout");
            }
            return StubHandler.Status(path.EndsWith("/12312021.php") ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        });

        var found = await Prober(handler).ProbeAsync("ch", "news", 2021, 2021, new HashSet<string>());

        Assert.Equal(2, attempts);
        Assert.Equal(["12312021"], found);   // 失敗的那個之後的候選照樣探完
    }

    [Fact]
    public async Task Detailed_SeparatesDefiniteAnswersFromNoAnswer()
    {
        // 「拿到 404」與「沒拿到有效回應」原本一律當成「不存在」,全部失敗時分不出來
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            return path switch
            {
                "/ch/news/01012021.php" => StubHandler.Status(HttpStatusCode.OK),          // 命中 → 接著探 A
                "/ch/news/01012021A.php" => StubHandler.Status(HttpStatusCode.Gone),       // 確定沒有
                "/ch/news/20210101.php" => StubHandler.Status(HttpStatusCode.Forbidden),   // 沒回答
                "/ch/news/01022021.php" => StubHandler.Status(HttpStatusCode.Conflict),    // 重試後仍 409 → 沒回答
                "/ch/news/20210102.php" => throw new HttpRequestException("reset"),         // 重試後仍連線錯誤
                "/ch/news/01032021.php" => throw new TaskCanceledException("timeout"),      // 重試後仍逾時
                "/ch/news/20210103.php" => StubHandler.Status(HttpStatusCode.ServiceUnavailable),
                "/ch/news/01042021.php" => StubHandler.Status(HttpStatusCode.MovedPermanently),   // 轉址 = 確定沒有
                _ => StubHandler.Status(HttpStatusCode.NotFound),
            };
        });
        var known = OrphanProber.DateCandidates(2021, 2021).Skip(8).ToHashSet();   // 只探 1/1～1/4 的 8 個

        var result = await Prober(handler).ProbeDetailedAsync("ch", "news", 2021, 2021, known);

        Assert.Equal(["01012021"], result.Found);
        Assert.Equal(9, result.Checks);   // 8 個日期 + 命中後的 A
        Assert.Equal(new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["HTTP 403"] = 1,
            ["HTTP 409"] = 1,
            ["HTTP 503"] = 1,
            ["connection error"] = 1,
            ["timeout"] = 1,
        }, result.Unanswered);
        Assert.Equal(5, result.UnansweredCount);
    }

    [Fact]
    public async Task Detailed_RetrySucceeding_CountsAsAnswered()
    {
        // 第一次 409、重試拿到 404:有回答,不算進 Unanswered
        var attempts = 0;
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/01012021.php") && ++attempts == 1
                ? StubHandler.Status(HttpStatusCode.Conflict)
                : StubHandler.Status(HttpStatusCode.NotFound));
        var known = OrphanProber.DateCandidates(2021, 2021).Skip(1).ToHashSet();

        var result = await Prober(handler).ProbeDetailedAsync("ch", "news", 2021, 2021, known);

        Assert.Equal(1, result.Checks);
        Assert.Equal(0, result.UnansweredCount);
    }

    [Fact]
    public async Task UserCancellation_MidProbe_Propagates_AndStopsRequesting()
    {
        // Ctrl-C 必須中止(CLI 回 130),不能被當成上面那種逾時吞掉後繼續探幾百個候選
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/01032021.php"))
            {
                cts.Cancel();
                throw new TaskCanceledException("cancelled by user");
            }
            return StubHandler.Status(HttpStatusCode.NotFound);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Prober(handler).ProbeAsync("ch", "news", 2021, 2021, new HashSet<string>(), ct: cts.Token));

        Assert.Equal(5, handler.Requests.Count);   // 01012021、20210101、01022021、20210102、01032021
    }

    [Fact]
    public async Task Progress_ReportsHitsAndEveryHundredProbes()
    {
        string[] existing = ["20210505", "20210505A"];
        var handler = new StubHandler(req =>
            StubHandler.Status(existing.Any(s => req.RequestUri!.AbsolutePath.EndsWith($"/{s}.php"))
                ? HttpStatusCode.OK : HttpStatusCode.NotFound));
        var messages = new List<string>();

        // 已知的 slug 不算進「probed」:1 月份 62 個候選全部當成已抓過
        var known = OrphanProber.DateCandidates(2021, 2021).Take(62).ToHashSet();
        await Prober(handler).ProbeAsync("ch", "news", 2021, 2021, known, messages.Add);

        Assert.Single(messages, m => m == "hit 20210505");
        Assert.Single(messages, m => m == "hit 20210505A");
        Assert.DoesNotContain("hit 20210505B", messages);
        var counters = messages.Where(m => m.StartsWith("probed ", StringComparison.Ordinal)).ToList();
        Assert.Equal(((365 * 2) - 62) / 100, counters.Count);
        // 5 月 5 日是扣掉 1 月後的第 188 個候選;A/B 後綴的探測不算進 probed
        Assert.Equal("probed 100, hits 0", counters[0]);
        Assert.Equal("probed 200, hits 2", counters[1]);
    }

    [Fact]
    public async Task UrlUsesFirstStripExtension_AndBaseUrlWithoutTrailingSlash()
    {
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.NotFound));
        var config = FastConfig();
        config.Site.BaseUrl = "https://old.example.org/";
        config.UrlPattern.StripExtensions = [".asp", ".php"];

        var known = OrphanProber.DateCandidates(2021, 2021).Skip(1).ToHashSet();
        await new OrphanProber(config, new HttpClient(handler))
            .ProbeAsync("en", "news", 2021, 2021, known);

        Assert.Equal(["HEAD https://old.example.org/en/news/01012021.asp"], handler.Requests);
    }

    [Fact]
    public async Task NoStripExtensions_ProbesExtensionlessUrls()
    {
        // 沒有動態副檔名的站(/news/20210101 這種乾淨網址):strip_extensions: [] 時 URL 不加任何尾綴
        var handler = new StubHandler(_ => StubHandler.Status(HttpStatusCode.NotFound));
        var config = FastConfig();
        config.UrlPattern.StripExtensions = [];

        var known = OrphanProber.DateCandidates(2021, 2021).Skip(1).ToHashSet();
        await new OrphanProber(config, new HttpClient(handler)).ProbeAsync("ch", "news", 2021, 2021, known);

        Assert.Equal(["HEAD https://www.ibps-austin.org/ch/news/01012021"], handler.Requests);
    }

    [Fact]
    public void KnownSlugs_ReadFromTheMirrorForThatLanguageOnly()
    {
        var raw = Directory.CreateTempSubdirectory("polymigrate-known").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(raw, "ch", "news"));
            Directory.CreateDirectory(Path.Combine(raw, "en", "news"));
            File.WriteAllText(Path.Combine(raw, "ch", "news", "20210101.php.html"), "");
            File.WriteAllText(Path.Combine(raw, "ch", "news", ".DS_Store"), "");
            File.WriteAllText(Path.Combine(raw, "en", "news", "01022021.php.html"), "");

            Assert.Equal(new HashSet<string> { "20210101" }, OrphanProber.KnownSlugs(raw, "ch", "news"));
            Assert.Empty(OrphanProber.KnownSlugs(raw, "ch", "events"));   // 沒抓過的 section = 全部要探
        }
        finally
        {
            Directory.Delete(raw, recursive: true);
        }
    }

    [Fact]
    public void CreateHttpClient_NoAuthWorkaround_SendsNoCookie_AndConfiguredUserAgent()
    {
        var config = FastConfig();
        config.Site.UserAgent = "PolyMigrateTest/1.0";
        config.Site.AuthWorkaround = new AuthWorkaroundSection { Type = "cookie", Set = [] };

        using var client = OrphanProber.CreateHttpClient(config);

        Assert.False(client.DefaultRequestHeaders.Contains("Cookie"));   // 空 set 不送空的 Cookie 標頭
        Assert.Equal("PolyMigrateTest/1.0", string.Join(' ', client.DefaultRequestHeaders.GetValues("User-Agent")));
    }

    [Fact]
    public void CreateHttpClient_CarriesAuthCookieAndUserAgent()
    {
        var config = FastConfig();
        config.Site.AuthWorkaround = new AuthWorkaroundSection
        {
            Type = "cookie",
            Set = new Dictionary<string, string> { ["humans_21909"] = "1" },
        };

        using var client = OrphanProber.CreateHttpClient(config);

        Assert.Equal("humans_21909=1", client.DefaultRequestHeaders.GetValues("Cookie").Single());
        Assert.Contains("Chrome", string.Join(' ', client.DefaultRequestHeaders.GetValues("User-Agent")));
    }
}
