using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Pairing;

/// <summary>
/// <c>hreflang_map.csv</c> 的一列:一則宣告,加上它經不經得起查證。
/// </summary>
/// <param name="InMirror">目標頁在鏡像裡(同 host、且該路由真的抽到過一頁)。</param>
/// <param name="Reciprocal">目標頁也宣告了一則指回來的 hreflang。</param>
/// <param name="Usable">可以拿來配對(見 <see cref="HreflangIndex"/> 的四道門)。</param>
internal sealed record HreflangRow(
    string SourceUrl,
    string Hreflang,
    string TargetUrl,
    string TargetKey,
    bool InMirror,
    bool Reciprocal,
    bool Usable);

/// <summary>
/// 把各頁宣告的 hreflang 解析成「哪個 translation_key 是哪個 key 的哪一語言版本」(§1.4)。
///
/// <para>hreflang 是全站唯一由作者<b>宣告</b>的配對關係——其餘線索(對稱路徑、共用相簿、
/// slug 日期、標題相似度)都是工具推測出來的。但「宣告的」不等於「對的」:舊站的 hreflang
/// 多半是後來 SEO 外包加的,常見三種壞法——整站 alternate 都指首頁、指到早就 404 的 URL、
/// 只有單向沒有互指。所以這裡只認過得了四道門的宣告:</para>
///
/// <list type="number">
///   <item>不是 <c>x-default</c>——那是「都不符合時去哪」,不是某個語言版本</item>
///   <item>不指向自己(自我指涉的 hreflang 是標準做法,但配對上零資訊)</item>
///   <item>目標頁真的在鏡像裡(同 host,且該路由抽得到一頁)</item>
///   <item>兩端都不是站級頁(<c>/</c> 開頭的 key,如語言選擇頁——它不是誰的翻譯)</item>
/// </list>
///
/// <para>刻意<b>不</b>要求互指:互指是可信度的加分,不是門檻——單向 hreflang 在真實站上
/// 太常見(常常只有預設語言那半宣告)。互指與否照實記在 <c>hreflang_map.csv</c> 的
/// <c>reciprocal</c> 欄,由人判斷。這一層只「建議」不合併,守門標準本來就該比
/// 直接覆寫 translation_key 寬。</para>
/// </summary>
internal sealed class HreflangIndex
{
    /// <summary>「以上皆非時去這裡」的特例值,不是語言(RFC 8288 / Google 定義)。</summary>
    public const string XDefault = "x-default";

    private HreflangIndex(List<HreflangRow> rows, Dictionary<string, Dictionary<string, string>> alternatesByKey)
    {
        Rows = rows;
        AlternatesByKey = alternatesByKey;
    }

    /// <summary>全部宣告,含不可用的;排序 = 來源 URL、hreflang、目標 URL(ordinal)。</summary>
    public List<HreflangRow> Rows { get; }

    /// <summary>可用宣告:來源 key → (目標 key → hreflang 值)。</summary>
    public Dictionary<string, Dictionary<string, string>> AlternatesByKey { get; }

    public int Declared => Rows.Count;

    public int Usable => Rows.Count(r => r.Usable);

    public static HreflangIndex Build(
        IEnumerable<HreflangLink> links, IReadOnlyDictionary<string, string> routeToKey)
    {
        var all = links.ToList();
        // 互指判定用「路由對」而非 URL 字串:同一頁可以被寫成 /en/news/a.php、/en/news/a、
        // 甚至 ../news/a.php,三種寫法都該算成同一頁(RouteForPath 已收攏副檔名與 index)。
        var declared = new HashSet<(string From, string To)>(
            all.Where(l => l.TargetRoute is not null).Select(l => (l.SourceRoute, l.TargetRoute!)));

        var resolved = new List<(HreflangLink Link, HreflangRow Row)>();
        foreach (var link in all)
        {
            var targetKey = link.TargetRoute is { } route ? routeToKey.GetValueOrDefault(route) : null;
            var inMirror = targetKey is not null;
            var reciprocal = link.TargetRoute is not null && declared.Contains((link.TargetRoute, link.SourceRoute));
            var usable = inMirror
                && !link.Hreflang.Equals(XDefault, StringComparison.OrdinalIgnoreCase)
                && targetKey != link.SourceKey
                && !targetKey!.StartsWith('/')
                && !link.SourceKey.StartsWith('/');
            resolved.Add((link, new HreflangRow(
                link.SourceUrl, link.Hreflang, link.TargetUrl, targetKey ?? "", inMirror, reciprocal, usable)));
        }

        resolved = [.. resolved
            .OrderBy(p => p.Row.SourceUrl, StringComparer.Ordinal)
            .ThenBy(p => p.Row.Hreflang, StringComparer.Ordinal)
            .ThenBy(p => p.Row.TargetUrl, StringComparer.Ordinal)];

        var byKey = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (link, row) in resolved.Where(p => p.Row.Usable))
        {
            if (!byKey.TryGetValue(link.SourceKey, out var targets))
            {
                targets = new Dictionary<string, string>(StringComparer.Ordinal);
                byKey[link.SourceKey] = targets;
            }
            // 同一對 key 被多個 hreflang 值連到時(en 與 en-US 並存)留先來的;上面已排過序,
            // 所以「先來的」是 hreflang 字典序第一個,不是檔案巡覽順序——輸出才是決定性的(§3.10)
            targets.TryAdd(row.TargetKey, row.Hreflang);
        }
        return new HreflangIndex([.. resolved.Select(p => p.Row)], byKey);
    }
}
