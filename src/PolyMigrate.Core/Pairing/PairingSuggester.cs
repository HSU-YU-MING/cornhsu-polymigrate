using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Pairing;

/// <summary>配對建議的候選:只有單一語言版本的 translation_key(§1.4「配不起來的」)。</summary>
internal sealed class UnpairedGroup
{
    public required string TranslationKey { get; init; }

    public required string Section { get; init; }

    public required string Locale { get; init; }

    public required string Slug { get; init; }

    public required string Title { get; init; }

    public required IReadOnlySet<string> Media { get; init; }

    /// <summary>本組頁面宣告、且通過查證的 hreflang:目標 translation_key → hreflang 值
    /// (見 <see cref="HreflangIndex"/>)。沒宣告 hreflang 的站是空的,這很常見。</summary>
    public IReadOnlyDictionary<string, string> Alternates { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>一筆建議:兩個互缺對方語言的 key,附證據(如 shared_media=3)。</summary>
internal sealed record PairSuggestion(string KeyA, string KeyB, string Evidence);

/// <summary>
/// 啟發式配對建議(§1.4):對稱路徑配不起來的內容(如檔名語意命名的活動頁),
/// 依 config pairing.fallback 順序用「共用媒體 / slug 日期 / 標題相似度」建議人工覆核的配對。
/// 只建議、不自動合併——最終決定權在人(content_inventory 的 final 欄)。
/// </summary>
internal sealed class PairingSuggester(SiteConfig config)
{
    public const string Hreflang = "hreflang";
    public const string SharedMedia = "shared_media";
    public const string Date = "date";
    public const string TitleSimilarity = "title_similarity";

    /// <summary>合法的 fallback 啟發式名稱——config 驗證與此處派發共用的單一事實來源。</summary>
    public static readonly IReadOnlySet<string> KnownHeuristics =
        new HashSet<string>(StringComparer.Ordinal) { Hreflang, SharedMedia, Date, TitleSimilarity };

    /// <summary>title_similarity 低於此值不當證據(跨語言標題相似度本就偏弱)。</summary>
    private const double TitleSimilarityThreshold = 0.5;

    public List<PairSuggestion> Suggest(IEnumerable<UnpairedGroup> unpaired)
    {
        var pool = unpaired.OrderBy(g => g.TranslationKey, StringComparer.Ordinal).ToList();
        var taken = new HashSet<string>();
        var suggestions = new List<PairSuggestion>();

        // 兩趟:作者宣告的 hreflang 先配完,啟發式才上場。
        // 同一趟裡是貪婪配對、先到先得,而「先到」只是 translation_key 的字典序——
        // 一趟做完的話,某個 key 會被排在前面的共用相簿先搶走,另一頭那則 hreflang
        // 就再也配不到對象了,等於讓推測的贏過宣告的。config 沒列 hreflang 時第一趟空轉。
        PairPass(requireHreflang: true);
        PairPass(requireHreflang: false);
        return suggestions;

        void PairPass(bool requireHreflang)
        {
            foreach (var a in pool)
            {
                if (taken.Contains(a.TranslationKey))
                {
                    continue;
                }
                // 同 section 是啟發式的護欄:共用相簿、同日期、標題像,跨 section 太容易巧合。
                // hreflang 不受這條限制——它是作者宣告的,而語言版本換 section 是真的會發生
                // (中文在 /ch/news/、英文在 /en/press/),那正是對稱路徑配不起來的原因之一。
                var candidates = pool.Where(b =>
                        !taken.Contains(b.TranslationKey)
                        && b.TranslationKey != a.TranslationKey
                        && b.Locale != a.Locale
                        && (b.Section == a.Section || HreflangToken(a, b) is not null)
                        && (!requireHreflang || HreflangToken(a, b) is not null))
                    .ToList();

                var best = candidates
                    .Select(b => (Group: b, Evidence: Evaluate(a, b)))
                    .Where(c => c.Evidence.Count > 0)
                    .OrderByDescending(c => Score(c.Evidence))
                    .ThenBy(c => c.Group.TranslationKey, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (best.Group is null)
                {
                    continue;
                }

                taken.Add(a.TranslationKey);
                taken.Add(best.Group.TranslationKey);
                suggestions.Add(new PairSuggestion(
                    a.TranslationKey, best.Group.TranslationKey,
                    string.Join(';', best.Evidence.Select(e => $"{e.Key}={e.Value}"))));
            }
        }
    }

    /// <summary>回傳命中的證據(依 fallback 順序);空 = 無任何依據,不建議。</summary>
    private List<KeyValuePair<string, string>> Evaluate(UnpairedGroup a, UnpairedGroup b)
    {
        var evidence = new List<KeyValuePair<string, string>>();
        foreach (var heuristic in config.Pairing.Fallback)
        {
            switch (heuristic)
            {
                case Hreflang:
                    if (HreflangToken(a, b) is { } token)
                    {
                        evidence.Add(new(Hreflang, token));
                    }
                    break;
                case SharedMedia:
                    var shared = a.Media.Intersect(b.Media, StringComparer.Ordinal).Count();
                    if (shared > 0)
                    {
                        evidence.Add(new(SharedMedia, shared.ToString()));
                    }
                    break;
                case Date:
                    if (SlugDates.FromSlug(a.Slug) is { } da && SlugDates.FromSlug(b.Slug) is { } db && da == db)
                    {
                        evidence.Add(new(Date, da.ToString("yyyy-MM-dd")));
                    }
                    break;
                case TitleSimilarity:
                    var sim = BigramDice(a.Title, b.Title);
                    if (sim >= TitleSimilarityThreshold)
                    {
                        evidence.Add(new(TitleSimilarity, sim.ToString("0.00")));
                    }
                    break;
            }
        }
        return evidence;
    }

    /// <summary>
    /// 兩端任一方宣告了指向對方的 hreflang → 回那個 hreflang 值,否則 null。
    /// <c>pairing.fallback</c> 沒列 hreflang 時一律回 null,行為與加這個啟發式之前逐位元相同。
    /// </summary>
    private string? HreflangToken(UnpairedGroup a, UnpairedGroup b) =>
        config.Pairing.Fallback.Contains(Hreflang)
            ? a.Alternates.GetValueOrDefault(b.TranslationKey)
              ?? b.Alternates.GetValueOrDefault(a.TranslationKey)
            : null;

    /// <summary>排序分數:fallback 越前面的證據權重越高;shared_media 另以數量加細分。</summary>
    private long Score(List<KeyValuePair<string, string>> evidence)
    {
        long score = 0;
        foreach (var e in evidence)
        {
            var rank = config.Pairing.Fallback.Count - config.Pairing.Fallback.IndexOf(e.Key);
            score += rank * 1_000_000L;
            if (e.Key == SharedMedia)
            {
                score += Math.Min(long.Parse(e.Value), 999_999);
            }
        }
        return score;
    }

    /// <summary>Sørensen–Dice 字元 bigram 相似度(0..1),不分大小寫。</summary>
    public static double BigramDice(string x, string y)
    {
        var bx = Bigrams(x);
        var by = Bigrams(y);
        if (bx.Count == 0 || by.Count == 0)
        {
            return 0;
        }
        var overlap = 0;
        foreach (var (gram, count) in bx)
        {
            overlap += Math.Min(count, by.GetValueOrDefault(gram));
        }
        return 2.0 * overlap / (bx.Values.Sum() + by.Values.Sum());
    }

    private static Dictionary<string, int> Bigrams(string s)
    {
        s = s.ToLowerInvariant();
        var grams = new Dictionary<string, int>();
        for (var i = 0; i + 1 < s.Length; i++)
        {
            var g = s.Substring(i, 2);
            grams[g] = grams.GetValueOrDefault(g) + 1;
        }
        return grams;
    }
}
