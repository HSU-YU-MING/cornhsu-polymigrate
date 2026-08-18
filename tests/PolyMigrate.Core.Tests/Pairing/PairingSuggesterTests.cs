using PolyMigrate.Core.Pairing;

namespace PolyMigrate.Core.Tests.Pairing;

public class PairingSuggesterTests
{
    private static readonly PairingSuggester Suggester = new(TestConfigs.IbpsLike());

    /// <summary>指定 fallback 順序的 suggester(TestConfigs 的預設不含 2.3 新增的線索)。</summary>
    private static PairingSuggester WithFallback(params string[] fallback)
    {
        var config = TestConfigs.IbpsLike();
        config.Pairing.Fallback = [.. fallback];
        return new PairingSuggester(config);
    }

    /// <summary>2.3 起 examples 與 README 的建議順序。</summary>
    private static PairingSuggester WithHreflang() =>
        WithFallback("hreflang", "slug_normalized", "shared_media", "date", "title_similarity");

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

    /// <summary>加上「本頁宣告了指向 targetKey 的 hreflang」。</summary>
    private static UnpairedGroup WithAlternate(UnpairedGroup group, string targetKey, string hreflang) =>
        new()
        {
            TranslationKey = group.TranslationKey,
            Section = group.Section,
            Locale = group.Locale,
            Slug = group.Slug,
            Title = group.Title,
            Media = group.Media,
            Alternates = new Dictionary<string, string>(StringComparer.Ordinal) { [targetKey] = hreflang },
        };

    [Fact]
    public void Hreflang_BeatsSharedMedia_WhenBothPresent()
    {
        // 作者宣告的關係贏過工具推測的:證據兩個都留,但排序由 fallback 順序決定
        var zh = WithAlternate(
            Group("events/2026_cjgx", "zh-Hant", "2026_cjgx", "禪淨共修", "events", "m/1.jpg"),
            "events/enChant", "en");
        var suggestions = WithHreflang().Suggest(
        [
            zh,
            Group("events/enChant", "en", "enChant", "Chanting Service", "events"),
            Group("events/decoy", "en", "decoy", "Decoy", "events", "m/1.jpg"),
        ]);

        var s = suggestions.Single(x => x.KeyA == "events/2026_cjgx" || x.KeyB == "events/2026_cjgx");
        Assert.Contains("events/enChant", new[] { s.KeyA, s.KeyB });   // 不是共用相簿的那個 decoy
        Assert.Contains("hreflang=en", s.Evidence);
    }

    [Fact]
    public void Hreflang_PairsFirst_EvenIfAHeuristicWouldHaveClaimedTheKeyEarlier()
    {
        // 貪婪配對是先到先得,而「先到」只是 key 的字典序:events/aaa 排在
        // events/zzz 前面,若一趟做完就會用共用相簿先把 events/enTarget 搶走,
        // 而 events/zzz 那則作者宣告的 hreflang 就再也配不到——推測贏過宣告。
        var suggestions = WithHreflang().Suggest(
        [
            Group("events/aaa", "zh-Hant", "aaa", "甲", "events", "m/1.jpg"),
            Group("events/enTarget", "en", "enTarget", "Target", "events", "m/1.jpg"),
            WithAlternate(Group("events/zzz", "zh-Hant", "zzz", "乙", "events"), "events/enTarget", "en"),
        ]);

        var s = Assert.Single(suggestions);
        Assert.Equal(
            ["events/enTarget", "events/zzz"],           // 不是字典序在前、共用相簿的 events/aaa
            new[] { s.KeyA, s.KeyB }.Order(StringComparer.Ordinal));
        Assert.Contains("hreflang=en", s.Evidence);
    }

    [Fact]
    public void Hreflang_PairsAcrossSections()
    {
        // 中文在 /ch/news/、英文在 /en/press/ 是真的會發生,而那正是對稱路徑配不起來的原因之一。
        // 啟發式不准跨 section(太容易巧合),hreflang 可以——它不是猜的。
        var suggestions = WithHreflang().Suggest(
        [
            WithAlternate(Group("news/a", "zh-Hant", "a", "甲", "news"), "press/b", "en"),
            Group("press/b", "en", "b", "A", "press"),
        ]);

        var s = Assert.Single(suggestions);
        Assert.Contains("hreflang=en", s.Evidence);
    }

    [Fact]
    public void Hreflang_OnlyDeclaredByOneSide_StillPairs()
    {
        var suggestions = WithHreflang().Suggest(
        [
            Group("news/a", "zh-Hant", "a", "甲", "news"),
            WithAlternate(Group("news/b", "en", "b", "A", "news"), "news/a", "zh-Hant"),
        ]);

        Assert.Contains("hreflang=zh-Hant", Assert.Single(suggestions).Evidence);
    }

    [Fact]
    public void Hreflang_NotInFallback_IsIgnoredEntirely()
    {
        // 沒把 hreflang 列進 pairing.fallback 的既有 config,行為必須與 2.2 完全相同
        var suggestions = Suggester.Suggest(
        [
            WithAlternate(Group("news/a", "zh-Hant", "a", "甲", "news"), "press/b", "en"),
            Group("press/b", "en", "b", "A", "press"),
        ]);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void SharedAlbum_SuggestsPair()
    {
        // 合成情境,**不是**香雲寺實況——這句原本寫成實況,是錯的。
        // 實際去翻那個鏡像:2026_cjgx 用 2026_sangha_cn.PNG、enChant 用 enChant.jpg,
        // 兩頁的圖不共用;整站 528 頁跑下來,shared_media 一組都沒配出來。
        // 保留這個測試是因為「兩語版共用相簿」在別的站上合理,但別再拿它當實證。
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
    public void SlugDifferingOnlyBySeparator_SuggestsPair()
    {
        // 香雲寺實況(這一組是真的,實跑 528 頁鏡像找出來的):光明燈法會
        // 中文 2025-light-offering、英文 2025_light_offering,只差一個分隔符,
        // 而 shared_media / date / title_similarity 三個全部落空。
        var suggester = WithFallback("slug_normalized", "shared_media", "date", "title_similarity");
        var suggestions = suggester.Suggest(
        [
            Group("events/2025-light-offering", "zh-Hant", "2025-light-offering", "2026光明燈法會"),
            Group("events/2025_light_offering", "en", "2025_light_offering", "Light Offering Service"),
        ]);

        Assert.Contains("slug_normalized=2025lightoffering", Assert.Single(suggestions).Evidence);
    }

    [Fact]
    public void SlugNormalized_RequiresExactEqualityAfterStripping_NotSimilarity()
    {
        // 這個啟發式不是「URL 相似度」:/products/ 與 /produkte/ 配不起來是對的,
        // 而 2026_lunar_new_year_celebrate 與 2026_lunar_new_year 也不該配
        // ——包含關係是換套衣服的相似度比對,一樣在猜。
        var suggester = WithFallback("slug_normalized");
        Assert.Empty(suggester.Suggest(
        [
            Group("events/2026_lunar_new_year_celebrate", "en", "2026_lunar_new_year_celebrate", "New Year"),
            Group("events/2026_lunar_new_year", "zh-Hant", "2026_lunar_new_year", "新春"),
        ]));
        Assert.Empty(suggester.Suggest(
        [
            Group("events/products", "en", "products", "Products"),
            Group("events/produkte", "de", "produkte", "Produkte"),
        ]));
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
