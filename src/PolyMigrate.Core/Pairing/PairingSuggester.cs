using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Pairing;

/// <summary>配對建議的候選:只有單一語言版本的 translation_key(§1.4「配不起來的」)。</summary>
public sealed class UnpairedGroup
{
    public required string TranslationKey { get; init; }

    public required string Section { get; init; }

    public required string Locale { get; init; }

    public required string Slug { get; init; }

    public required string Title { get; init; }

    public required IReadOnlySet<string> Media { get; init; }
}

/// <summary>一筆建議:兩個互缺對方語言的 key,附證據(如 shared_media=3)。</summary>
public sealed record PairSuggestion(string KeyA, string KeyB, string Evidence);

/// <summary>
/// 啟發式配對建議(§1.4):對稱路徑配不起來的內容(如檔名語意命名的活動頁),
/// 依 config pairing.fallback 順序用「共用媒體 / slug 日期 / 標題相似度」建議人工覆核的配對。
/// 只建議、不自動合併——最終決定權在人(content_inventory 的 final 欄)。
/// </summary>
public sealed class PairingSuggester(SiteConfig config)
{
    public const string SharedMedia = "shared_media";
    public const string Date = "date";
    public const string TitleSimilarity = "title_similarity";

    /// <summary>title_similarity 低於此值不當證據(跨語言標題相似度本就偏弱)。</summary>
    private const double TitleSimilarityThreshold = 0.5;

    public List<PairSuggestion> Suggest(IEnumerable<UnpairedGroup> unpaired)
    {
        var pool = unpaired.OrderBy(g => g.TranslationKey, StringComparer.Ordinal).ToList();
        var taken = new HashSet<string>();
        var suggestions = new List<PairSuggestion>();

        foreach (var a in pool)
        {
            if (taken.Contains(a.TranslationKey))
            {
                continue;
            }
            var candidates = pool.Where(b =>
                    !taken.Contains(b.TranslationKey)
                    && b.TranslationKey != a.TranslationKey
                    && b.Section == a.Section
                    && b.Locale != a.Locale)
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
        return suggestions;
    }

    /// <summary>回傳命中的證據(依 fallback 順序);空 = 無任何依據,不建議。</summary>
    private List<KeyValuePair<string, string>> Evaluate(UnpairedGroup a, UnpairedGroup b)
    {
        var evidence = new List<KeyValuePair<string, string>>();
        foreach (var heuristic in config.Pairing.Fallback)
        {
            switch (heuristic)
            {
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
