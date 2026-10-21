using System.Globalization;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

public class TitleCleanerTests
{
    private static readonly TitleCleaner Cleaner = new(TestConfigs.IbpsLike().Extract);

    [Theory]
    // 日期前綴三式(§2.6:YYYYMMDD 與 MMDDYYYY 混用都要認)
    [InlineData("2025/02/02 新聞:八關齋戒", "八關齋戒")]
    [InlineData("02/02/2025新聞:八關齋戒", "八關齋戒")]
    [InlineData("News 02/02/2025: Winter Retreat", "Winter Retreat")]
    [InlineData("2025/02/02:八關齋戒", "八關齋戒")]
    public void DatePrefixes_StrippedOnBodyTitles(string raw, string expected) =>
        Assert.Equal(expected, Cleaner.Clean(raw, stripSiteNoise: false));

    [Fact]
    public void TitleTag_SiteNoiseStripped()
    {
        Assert.Equal("About Us", Cleaner.Clean("佛光山香雲寺 - About Us", stripSiteNoise: true));
        Assert.Equal("聯絡我們", Cleaner.Clean("聯絡我們 | 佛光山香雲寺", stripSiteNoise: true));
    }

    [Fact]
    public void TitleTag_NewsMarker_TakesTextAfterMarker()
    {
        // 站名+日期在前時,直接取「新聞:/News:」之後的正文最穩(規格 clean_title)
        Assert.Equal("浴佛法會",
            Cleaner.Clean("佛光山香雲寺 2025/05/04 新聞:浴佛法會", stripSiteNoise: true));
    }

    [Fact]
    public void BodyTitle_SiteNameKept()
    {
        // 英文真標題本來就可能以寺名開頭,strip_site=False 不可刪
        const string t = "Fo Guang Shan Xiang Yun Temple in Austin Hosts Winter Retreat";
        Assert.Equal(t, Cleaner.Clean(t, stripSiteNoise: false));
    }

    [Fact]
    public void EmptyOrNull_YieldsEmpty()
    {
        Assert.Equal("", Cleaner.Clean(null, stripSiteNoise: true));
        Assert.Equal("", Cleaner.Clean("  ", stripSiteNoise: true));
    }

    [Fact]
    public void TitleMarkers_AreConfigurable_NotHardcodedToZhEn()
    {
        // i18n-first:標記字可換成任何語言
        var config = TestConfigs.IbpsLike();
        config.Extract.TitleMarkers = ["ニュース"];
        var cleaner = new TitleCleaner(config.Extract);

        Assert.Equal("春の法要", cleaner.Clean("2026/04/01 ニュース:春の法要", stripSiteNoise: false));
        // 換掉後,預設的 News 不再是標記字(僅日期前綴仍剝)
        Assert.Equal("News:Retreat", cleaner.Clean("News:Retreat", stripSiteNoise: false));
    }

    [Fact]
    public void EmptyTitleMarkers_DisablesMarkerStripping_KeepsDatePrefixes()
    {
        var config = TestConfigs.IbpsLike();
        config.Extract.TitleMarkers = [];
        var cleaner = new TitleCleaner(config.Extract);

        Assert.Equal("八關齋戒", cleaner.Clean("2025/02/02:八關齋戒", stripSiteNoise: false));
        Assert.Equal("News 02/02/2025: Retreat", cleaner.Clean("News 02/02/2025: Retreat", stripSiteNoise: false));
    }

    [Fact]
    public void MarkerMatching_DoesNotDependOnMachineCulture()
    {
        // IgnoreCase 一律搭 CultureInvariant:tr-TR 上 'i' 的大寫是 'İ' 不是 'I',
        // 少了 CultureInvariant 的話「NOTICE:」在土耳其語系的機器上就不再被當成標記字——
        // 同一份 config、同一個站,輸出隨跑的機器而變(與 §3.10 決定性輸出相悖)。
        // regex 在建構時抓取文化,所以 TitleCleaner 必須在切換文化之後才建
        var original = CultureInfo.CurrentCulture;
        string cleaned;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var config = TestConfigs.IbpsLike();
            config.Extract.TitleMarkers = ["Notice"];
            cleaned = new TitleCleaner(config.Extract).Clean("IBPS Austin NOTICE: Winter Retreat", stripSiteNoise: true);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal("Winter Retreat", cleaned);
    }

    [Fact]
    public void NoTitleMarkers_SiteNoiseStillStrippedFromTitleTag()
    {
        // title_markers: [] 只關掉「標記字:」那條捷徑,站名雜訊照剝
        var config = TestConfigs.IbpsLike();
        config.Extract.TitleMarkers = [];
        var cleaner = new TitleCleaner(config.Extract);

        Assert.Equal("About Us", cleaner.Clean("IBPS Austin | About Us", stripSiteNoise: true));
    }

    [Fact]
    public void ConfiguredStripTitlePrefix_CombinesWithBuiltInDatePrefix()
    {
        var config = TestConfigs.IbpsLike();
        config.Extract.StripTitlePrefix = [@"^\[[^\]]+\]\s*"];
        var cleaner = new TitleCleaner(config.Extract);

        Assert.Equal("浴佛法會", cleaner.Clean("[公告] 2025/05/04 新聞:浴佛法會", stripSiteNoise: false));
    }

    [Fact]
    public void EmptyNoiseEntry_DoesNotHang()
    {
        // config 若含空字串雜訊,StartsWith("")/EndsWith("") 恆真會讓剝除迴圈永不收斂 → 必須先濾掉
        var config = TestConfigs.IbpsLike();
        config.Extract.TitleNoise = ["", "香雲寺"];
        var cleaner = new TitleCleaner(config.Extract);

        Assert.Equal("關於", cleaner.Clean("香雲寺 - 關於", stripSiteNoise: true));
    }
}
