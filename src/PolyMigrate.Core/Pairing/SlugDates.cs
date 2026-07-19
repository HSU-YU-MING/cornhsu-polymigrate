using System.Text.RegularExpressions;

namespace PolyMigrate.Core.Pairing;

/// <summary>
/// slug 內的日期抽取與正規化(§2.6:原站 YYYYMMDD 與 MMDDYYYY 混用,兩種都認)。
/// </summary>
public static partial class SlugDates
{
    /// <summary>自 slug 抽第一段 8 位數字並解讀為日期;認不得回傳 null。</summary>
    public static DateOnly? FromSlug(string slug)
    {
        var m = EightDigits().Match(slug);
        if (!m.Success)
        {
            return null;
        }
        var d = m.Value;
        // 依序試 YYYYMMDD → MMDDYYYY → DDMMYYYY(歐系)。
        // MDY/DMY 皆合法的歧義值(如 01022026)偏向 MDY——來源資料以美式為主;僅作建議、人工覆核把關。
        if (TryYmd(d[..4], d[4..6], d[6..8], out var ymd))
        {
            return ymd;
        }
        if (TryYmd(d[4..8], d[..2], d[2..4], out var mdy))
        {
            return mdy;
        }
        if (TryYmd(d[4..8], d[2..4], d[..2], out var dmy))
        {
            return dmy;
        }
        return null;
    }

    private static bool TryYmd(string year, string month, string day, out DateOnly date)
    {
        date = default;
        if (int.Parse(year) is < 1990 or > 2100)
        {
            return false;
        }
        return DateOnly.TryParseExact($"{year}-{month}-{day}", "yyyy-MM-dd", out date);
    }

    [GeneratedRegex(@"\d{8}")]
    private static partial Regex EightDigits();
}
