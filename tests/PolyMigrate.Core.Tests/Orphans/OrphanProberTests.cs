using System.Net;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Orphans;

namespace PolyMigrate.Core.Tests.Orphans;

public class OrphanProberTests
{
    private static SiteConfig FastConfig()
    {
        var config = TestConfigs.IbpsLike();
        config.Site.Polite = new PoliteSection { Concurrency = 1, DelayMs = 0 };
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
