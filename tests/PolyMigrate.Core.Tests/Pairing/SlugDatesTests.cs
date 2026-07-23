using PolyMigrate.Core.Pairing;

namespace PolyMigrate.Core.Tests.Pairing;

public class SlugDatesTests
{
    [Theory]
    // §2.6:YYYYMMDD 與 MMDDYYYY 混用,兩種都認、正規化
    [InlineData("20260118", 2026, 1, 18)]
    [InlineData("01182026", 2026, 1, 18)]
    [InlineData("01212024C", 2024, 1, 21)]   // 香雲寺實例:英文版帶尾綴字母
    [InlineData("news-20251104-x", 2025, 11, 4)]
    public void EightDigitRuns_Normalized(string slug, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), SlugDates.FromSlug(slug));

    [Theory]
    [InlineData("2026_cjgx")]        // 語意命名,無日期
    [InlineData("enChant")]
    [InlineData("99999999")]         // 非法日期
    [InlineData("12345")]
    [InlineData("２０２４０１１８")]    // 全形數字:[0-9] 不吃,不得誤判為日期、更不得炸
    [InlineData("news-٢٠٢٤٠١٠١")]    // 阿拉伯數字同理
    public void NonDates_ReturnNull(string slug) =>
        Assert.Null(SlugDates.FromSlug(slug));

    [Fact]
    public void AmbiguousButValidAsYmd_PrefersYmd() =>
        // 20120505:YYYYMMDD 與 MMDDYYYY 皆合法 → 取 YMD(原站主流格式)
        Assert.Equal(new DateOnly(2012, 5, 5), SlugDates.FromSlug("20120505"));

    [Fact]
    public void EuropeanDdMmYyyy_Recognized() =>
        // 14022026:MDY 不合法(月 14)→ 落到歐系 DDMMYYYY
        Assert.Equal(new DateOnly(2026, 2, 14), SlugDates.FromSlug("14022026"));

    [Fact]
    public void MdyDmyAmbiguity_PrefersMdy() =>
        Assert.Equal(new DateOnly(2026, 1, 2), SlugDates.FromSlug("01022026"));
}
