using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Tests.Configuration;

public class SiteConfigTests
{
    [Fact]
    public void Defaults_MatchPlannedSpec()
    {
        var config = new SiteConfig
        {
            Site = new SiteSection { BaseUrl = new Uri("https://example.org") },
            UrlPattern = new UrlPatternSection
            {
                LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en" },
                DefaultLang = "zh-Hant",
            },
            Extract = new ExtractSection { Content = "main" },
        };

        Assert.Equal(1, config.ConfigVersion);
        Assert.Equal(RenderMode.Static, config.Site.Render);
        Assert.Equal(1, config.Site.Polite.Concurrency);
        Assert.Equal(3000, config.Site.Polite.DelayMs);
        Assert.Equal(PairingStrategy.SymmetricPath, config.Pairing.Strategy);
        Assert.True(config.Media.Download);
        Assert.Equal("zh-Hant", config.UrlPattern.LangMap["ch"]);
    }
}
