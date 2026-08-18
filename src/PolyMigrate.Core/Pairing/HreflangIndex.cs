using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Pairing;

/// <summary>
/// <c>hreflang_map.csv</c> 的一列:一則宣告,加上它經不經得起查證。
/// </summary>
/// <param name="InMirror">目標頁在鏡像裡(同 host、且該路由真的抽到過一頁)。</param>
/// <param name="Reciprocal">目標頁也宣告了一則指回來的 hreflang。</param>
/// <param name="Usable">通過全部門檻,可用於配對(不代表一定產生了建議)。</param>
/// <param name="RejectReason">不可用的原因,見 <see cref="HreflangIndex"/> 的常數;可用時為空字串。</param>
internal sealed record HreflangRow(
    string SourceUrl,
    string Hreflang,
    string TargetUrl,
    string TargetKey,
    bool InMirror,
    bool Reciprocal,
    bool Usable,
    string RejectReason);

/// <summary>
/// 把各頁宣告的 hreflang 解析成「哪個 translation_key 是哪個 key 的哪一語言版本」(§1.4)。
///
/// <para>hreflang 是全站唯一由作者<b>宣告</b>的配對關係——其餘線索(對稱路徑、共用相簿、
/// slug 日期、標題相似度)都是工具推測出來的。但「宣告的」不等於「對的」:舊站的 hreflang
/// 多半是後來 SEO 外包加的,壞掉的比對的多。所以一則宣告要能用於配對,必須通過
/// <see cref="RejectReason"/> 與 <see cref="AmbiguousTarget"/> 列出的每一道門——
/// 那幾個常數就是門檻的唯一事實來源,不可用的原因也照實輸出到 <c>reject_reason</c> 欄,
/// 讓「這個站的 hreflang 能不能信」有資料可答,而不是只有一個總數。</para>
///
/// <para>刻意<b>不</b>要求互指:互指是可信度的加分,不是門檻——單向 hreflang 在真實站上
/// 太常見(常常只有預設語言那半宣告)。互指與否照實記在 <c>reciprocal</c> 欄,由人判斷。
/// 這一層只「建議」不合併,守門標準本來就該比直接覆寫 translation_key 寬。</para>
///
/// <para>「為什麼不乾脆用 hreflang 直接覆寫 translation_key」是這個檔案最常被問的問題,
/// 答案(含量測數字與重啟條件)在 <c>docs/hreflang_量測與決策.md</c>——
/// 那是刻意的決定,不是還沒做完。</para>
/// </summary>
internal sealed class HreflangIndex
{
    /// <summary>「以上皆非時去這裡」的特例值,不是語言(RFC 8288 / Google 定義)。</summary>
    public const string XDefault = "x-default";

    /// <summary>目標頁不在鏡像裡:指到別的 host,或指到一個原站早就下架的 URL。</summary>
    public const string NotInMirror = "not_in_mirror";

    /// <summary><c>x-default</c> 不是某個語言版本,是「都不符合時去哪」。</summary>
    public const string IsXDefault = "x_default";

    /// <summary>指向自己。自我指涉是標準做法,但配對上零資訊。</summary>
    public const string SelfReference = "self_reference";

    /// <summary>兩端任一是站級頁(<c>/</c> 開頭的 key,如語言選擇頁)——它不是誰的翻譯。</summary>
    public const string SiteLevel = "site_level";

    /// <summary>
    /// 同一個目標頁被<b>多個同語言</b>的來源頁宣告為語言版本——一頁不可能是三頁的翻譯。
    ///
    /// <para>這是舊站最常見的壞法:SEO 外包把同一段 <c>&lt;link&gt;</c> 複製到每一頁的模板裡,
    /// 於是整站的 alternate 都指向首頁。少了這道門,那種站會產出「英文首頁 = 某篇中文新聞」
    /// 這種建議,而且掛著 hreflang 這個最權威的證據標籤,人最容易直接採信——
    /// 比誠實回報 missing 糟得多。</para>
    ///
    /// <para>限定「同語言」是因為多語站的合法情況長得很像:中文版與日文版可以<b>各自</b>
    /// 宣告同一個英文版(<c>ch → en</c>、<c>jp → en</c>),那是對的,不能一起擋掉。</para>
    /// </summary>
    public const string AmbiguousTarget = "ambiguous_target";

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
        var reciprocalPairs = new HashSet<(string From, string To)>(
            all.Where(l => l.TargetRoute is not null).Select(l => (l.SourceRoute, l.TargetRoute!)));

        // 第一輪:逐則判斷。ambiguous_target 要看過全部才知道,留到第二輪。
        var resolved = all
            .Select(link =>
            {
                var targetKey = link.TargetRoute is { } route ? routeToKey.GetValueOrDefault(route) : null;
                return (Link: link, TargetKey: targetKey, Reason: RejectReason(link, targetKey));
            })
            .ToList();

        // 第二輪:同一個 (目標 key, 來源語言) 若有多個不同來源 key 宣告,那些宣告全部作廢
        var claimants = resolved
            .Where(r => r.Reason is null)
            .GroupBy(r => (r.TargetKey, r.Link.SourceLocale))
            .Where(g => g.Select(r => r.Link.SourceKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var rows = resolved
            .Select(r =>
            {
                var reason = r.Reason
                    ?? (claimants.Contains((r.TargetKey, r.Link.SourceLocale)) ? AmbiguousTarget : null);
                return (r.Link, Row: new HreflangRow(
                    r.Link.SourceUrl, r.Link.Hreflang, r.Link.TargetUrl, r.TargetKey ?? "",
                    InMirror: r.TargetKey is not null,
                    Reciprocal: r.Link.TargetRoute is not null
                                && reciprocalPairs.Contains((r.Link.TargetRoute, r.Link.SourceRoute)),
                    Usable: reason is null,
                    RejectReason: reason ?? ""));
            })
            .OrderBy(p => p.Row.SourceUrl, StringComparer.Ordinal)
            .ThenBy(p => p.Row.Hreflang, StringComparer.Ordinal)
            .ThenBy(p => p.Row.TargetUrl, StringComparer.Ordinal)
            .ToList();

        var byKey = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (link, row) in rows.Where(p => p.Row.Usable))
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
        return new HreflangIndex([.. rows.Select(p => p.Row)], byKey);
    }

    /// <summary>逐則就能判斷的門檻;null = 這一則自己沒問題(還要再過 ambiguous_target)。</summary>
    private static string? RejectReason(HreflangLink link, string? targetKey) =>
        targetKey is null ? NotInMirror
        : link.Hreflang.Equals(XDefault, StringComparison.OrdinalIgnoreCase) ? IsXDefault
        : targetKey == link.SourceKey ? SelfReference
        : targetKey.StartsWith('/') || link.SourceKey.StartsWith('/') ? SiteLevel
        : null;
}
