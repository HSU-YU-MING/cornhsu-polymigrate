using System.Text.RegularExpressions;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 標題清理(規格 clean_title / _strip_date_prefix):去「YYYY/MM/DD 新聞:」類日期前綴;
/// 站名雜訊只在取 &lt;title&gt; 標籤時剝(stripSiteNoise=true)——內文真標題可能合法以站名開頭,不可剝。
/// 「新聞/News」類標記字由 config extract.title_markers 提供(i18n-first:不硬編任何語言的字)。
/// </summary>
internal sealed class TitleCleaner
{
    private static readonly char[] EdgeTrimChars = [.. " \t—|·-:："];
    private static readonly char[] PrefixTrimChars = [.. " \t—|·：:"];

    private readonly string[] _noise;
    private readonly Regex[] _prefixes;
    private readonly Regex? _marker;   // null = 無標記字,略過標記式清理

    public TitleCleaner(ExtractSection extract)
    {
        // 空字串會讓 StartsWith("")/EndsWith("") 永遠成立 → 下面 while 迴圈永不收斂,先濾掉
        _noise = [.. extract.TitleNoise.Where(n => n.Length > 0)];

        var markers = extract.TitleMarkers.Where(m => m.Length > 0).ToList();
        var alternation = string.Join('|', markers.Select(Regex.Escape));
        var optionalMarker = markers.Count > 0 ? $@"(?:{alternation})?" : "";

        // IgnoreCase 一律搭 CultureInvariant:否則 tr-TR 上的 Turkish-I 會讓 "News" 之類標記字
        // 在不同機器 locale 下清出不同結果(與 i18n 決定性目標相悖)
        const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // 日期+標記前綴三式:2025/02/02 新聞:|News 02/02/2025:|02/02/2025新聞:|2025/02/02:等
        List<Regex> prefixes =
        [
            new($@"^\s*\d{{4}}[/.\-]\d{{1,2}}[/.\-]\d{{1,2}}\s*{optionalMarker}\s*[:：]?\s*", Opts),
            new($@"^\s*\d{{1,2}}[/.\-]\d{{1,2}}[/.\-]\d{{4}}\s*{optionalMarker}\s*[:：]?\s*", Opts),
        ];
        if (markers.Count > 0)
        {
            prefixes.Add(new Regex(
                $@"^\s*(?:{alternation})\s*[:：]?\s*\d{{1,4}}[/.\-]\d{{1,2}}[/.\-]\d{{1,4}}\s*[:：]?\s*",
                Opts));
            _marker = new Regex($@"(?:{alternation})\s*[:：]\s*", Opts);
        }
        prefixes.AddRange(extract.StripTitlePrefix.Select(p => new Regex(p, Opts)));
        _prefixes = [.. prefixes];
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
            // 若含「新聞:/News:」類標記,直接取標記之後的正文(最穩,避開站名+日期在前)
            if (_marker?.Match(t) is { Success: true } m)
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
                        t = t[noise.Length..].TrimStart(EdgeTrimChars);
                        changed = true;
                    }
                    if (t.EndsWith(noise, StringComparison.Ordinal))
                    {
                        t = t[..^noise.Length].TrimEnd(EdgeTrimChars);
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
            t = t.Trim(PrefixTrimChars);
            if (t == before)
            {
                break;
            }
        }
        return t.Trim();
    }
}
