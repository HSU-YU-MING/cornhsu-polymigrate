using PolyMigrate.Core.Pairing;

namespace PolyMigrate.Core.Tests.Pairing;

public class PairingSuggesterTests
{
    private static readonly PairingSuggester Suggester = new(TestConfigs.IbpsLike());

    private static UnpairedGroup Group(string key, string locale, string slug,
        string title = "", string section = "events", params string[] media) => new()
        {
            TranslationKey = key,
            Section = section,
            Locale = locale,
            Slug = slug,
            Title = title,
            Media = new HashSet<string>(media),
        };

    [Fact]
    public void SharedAlbum_SuggestsPair()
    {
        // 香雲寺實況(§1.4):活動檔名中英不同(2026_cjgx / enChant),但相簿中英共用
        var suggestions = Suggester.Suggest(
        [
            Group("events/2026_cjgx", "zh-Hant", "2026_cjgx", "禪淨共修", "events",
                "ch/events/images/cjgx_1.jpg", "ch/events/images/cjgx_2.jpg"),
            Group("events/enChant", "en", "enChant", "Chanting Service", "events",
                "ch/events/images/cjgx_1.jpg"),
            Group("events/other", "en", "other", "Unrelated", "events",
                "ch/events/images/other.jpg"),
        ]);

        var s = Assert.Single(suggestions);
        Assert.Equal("events/2026_cjgx", s.KeyA);
        Assert.Equal("events/enChant", s.KeyB);
        Assert.Contains("shared_media=1", s.Evidence);
    }

    [Fact]
    public void SameSlugDate_SuggestsPair_AcrossDateFormats()
    {
        // §2.6:YYYYMMDD(中)對 MMDDYYYY(英)也要配得起來
        var suggestions = Suggester.Suggest(
        [
            Group("news/20240121", "zh-Hant", "20240121", "禮千佛法會", "news"),
            Group("news/01212024C", "en", "01212024C", "Thousand Buddhas", "news"),
        ]);

        var s = Assert.Single(suggestions);
        Assert.Contains("date=2024-01-21", s.Evidence);
    }

    [Fact]
    public void DifferentSection_NeverSuggested()
    {
        var suggestions = Suggester.Suggest(
        [
            Group("events/a", "zh-Hant", "a", "x", "events", "m/1.jpg"),
            Group("news/b", "en", "b", "x", "news", "m/1.jpg"),
        ]);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void SameLocale_NeverSuggested()
    {
        var suggestions = Suggester.Suggest(
        [
            Group("events/a", "zh-Hant", "a", "x", "events", "m/1.jpg"),
            Group("events/b", "zh-Hant", "b", "x", "events", "m/1.jpg"),
        ]);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void NoEvidence_NoSuggestion()
    {
        var suggestions = Suggester.Suggest(
        [
            Group("events/2026_cjgx", "zh-Hant", "2026_cjgx", "禪淨共修", "events", "a.jpg"),
            Group("events/enRetreat", "en", "enRetreat", "Retreat", "events", "b.jpg"),
        ]);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void MoreSharedMedia_WinsOverFewer()
    {
        var suggestions = Suggester.Suggest(
        [
            Group("events/zh1", "zh-Hant", "zh1", "甲", "events", "m/1.jpg", "m/2.jpg", "m/3.jpg"),
            Group("events/en-weak", "en", "en-weak", "Weak", "events", "m/1.jpg"),
            Group("events/en-strong", "en", "en-strong", "Strong", "events", "m/1.jpg", "m/2.jpg", "m/3.jpg"),
        ]);

        var first = suggestions.First(s => s.KeyA == "events/zh1" || s.KeyB == "events/zh1");
        Assert.Contains("events/en-strong", new[] { first.KeyA, first.KeyB });
        Assert.Contains("shared_media=3", first.Evidence);
    }

    [Fact]
    public void GreedyMatching_EachKeyUsedOnce()
    {
        var suggestions = Suggester.Suggest(
        [
            Group("news/20240101", "zh-Hant", "20240101", "元旦", "news"),
            Group("news/01012024", "en", "01012024", "New Year", "news"),
            Group("news/20240102", "zh-Hant", "20240102", "初二", "news"),
        ]);

        Assert.Single(suggestions);   // 20240102 沒有可配對象,不硬配
    }

    [Fact]
    public void TitleSimilarity_UsedAsLastResort()
    {
        var suggestions = Suggester.Suggest(
        [
            Group("events/light2026", "zh-Hant", "light2026", "Light Offering Festival 2026", "events"),
            Group("events/lightOffering", "en", "lightOffering", "Light Offering Festival", "events"),
        ]);

        var s = Assert.Single(suggestions);
        Assert.Contains("title_similarity=", s.Evidence);
    }
}
