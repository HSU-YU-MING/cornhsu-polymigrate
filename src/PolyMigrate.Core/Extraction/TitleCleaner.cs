using System.Text.RegularExpressions;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 標題清理(規格 clean_title / _strip_date_prefix):去「YYYY/MM/DD 新聞:」類日期前綴;
/// 站名雜訊只在取 &lt;title&gt; 標籤時剝(stripSiteNoise=true)——內文真標題可能合法以站名開頭,不可剝。
/// </summary>
public sealed partial class TitleCleaner
{
    // 日期+新聞前綴三式:2025/02/02 新聞:|News 02/02/2025:|02/02/2025新聞:|2025/02/02:等
    private static readonly Regex[] DefaultDatePrefixes =
    [
        YmdPrefix(),
        DmyPrefix(),
        MarkerFirstPrefix(),
    ];

    private static readonly Regex NewsMarker = NewsMarkerRegex();

    private const string EdgeTrimChars = " \t—|·-:：";
    private const string PrefixTrimChars = " \t—|·：:";

    private readonly string[] _noise;
    private readonly Regex[] _prefixes;

    public TitleCleaner(SiteConfig config)
    {
        _noise = [.. config.Extract.TitleNoise];
        _prefixes =
        [
            .. DefaultDatePrefixes,
            .. config.Extract.StripTitlePrefix.Select(p => new Regex(p, RegexOptions.IgnoreCase)),
        ];
    }

    public string Clean(string? title, bool stripSiteNoise)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }
        var t = title.Trim();

        if (stripSiteNoise)
        {
            // 若含「新聞:/News:」標記,直接取標記之後的正文(最穩,避開站名+日期在前)
            var m = NewsMarker.Match(t);
            if (m.Success)
            {
                return StripDatePrefix(t[(m.Index + m.Length)..].Trim());
            }
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var noise in _noise)
                {
                    if (t.StartsWith(noise, StringComparison.Ordinal))
                    {
                        t = t[noise.Length..].TrimStart(EdgeTrimChars.ToCharArray());
                        changed = true;
                    }
                    if (t.EndsWith(noise, StringComparison.Ordinal))
                    {
                        t = t[..^noise.Length].TrimEnd(EdgeTrimChars.ToCharArray());
                        changed = true;
                    }
                }
            }
        }
        return StripDatePrefix(t);
    }

    private string StripDatePrefix(string t)
    {
        for (var i = 0; i < 3; i++)
        {
            var before = t;
            foreach (var pattern in _prefixes)
            {
                t = pattern.Replace(t, "");
            }
            t = t.Trim(PrefixTrimChars.ToCharArray());
            if (t == before)
            {
                break;
            }
        }
        return t.Trim();
    }

    [GeneratedRegex(@"^\s*\d{4}[/.\-]\d{1,2}[/.\-]\d{1,2}\s*(新聞|News)?\s*[:：]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex YmdPrefix();

    [GeneratedRegex(@"^\s*\d{1,2}[/.\-]\d{1,2}[/.\-]\d{4}\s*(新聞|News)?\s*[:：]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DmyPrefix();

    [GeneratedRegex(@"^\s*(News|新聞)\s*[:：]?\s*\d{1,4}[/.\-]\d{1,2}[/.\-]\d{1,4}\s*[:：]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerFirstPrefix();

    [GeneratedRegex(@"(新聞|News)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NewsMarkerRegex();
}
