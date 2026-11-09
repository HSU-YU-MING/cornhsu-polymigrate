using System.Text;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Pairing;

internal enum DecisionKind
{
    /// <summary>這兩個 key 是同一篇的不同語言版(確認建議,或人工找到的對應)。</summary>
    Pair,

    /// <summary>這兩個 key 不是同一篇(否決建議;啟發式會改找下一個候選)。</summary>
    NotPair,

    /// <summary>這頁確定沒有其他語言版本(不再列為 missing)。</summary>
    Single,
}

internal sealed record PairingDecision(int Line, string Key, DecisionKind Kind, string? Other, string Note);

/// <summary>決定檔裡一個 key 在這次 inventory 的狀態(套用前的檢查用)。null = 這次的 inventory 沒有它。</summary>
internal sealed record DecisionKeyState(bool SiteLevel, bool Paired, string? Locale);

/// <summary>套用結果。<see cref="Unapplied"/> 是這次沒套上的列(站改了、key 打錯…),回報給人看,不默默略過。</summary>
internal sealed record DecisionOutcome(
    IReadOnlyDictionary<string, string> Confirmed,
    IReadOnlySet<string> Single,
    IReadOnlySet<(string, string)> Rejected,
    IReadOnlyDictionary<string, string> Notes,
    IReadOnlyList<string> Unapplied)
{
    public static readonly DecisionOutcome None = new(
        new Dictionary<string, string>(), new HashSet<string>(), new HashSet<(string, string)>(),
        new Dictionary<string, string>(), []);
}

/// <summary>
/// 人工配對決定檔(<c>pairing.decisions</c>)。<c>content_inventory.csv</c> 每次 extract 都整份重寫,
/// 人在上面做的決定沒有地方放——這份檔案就是那個固定的落點:extract 每次都讀、從不寫。
/// <para>決定只影響<b>覆核狀態</b>(pair_status、建議要不要再出現),不改寫 <c>translation_key</c>:
/// 它流進 frontmatter、redirect_map 與使用者自己的匯入程式,改寫的理由與風險見 docs/hreflang_量測與決策.md。</para>
/// </summary>
internal sealed class PairingDecisions
{
    public static readonly string[] Columns = ["translation_key", "decision", "other_key", "note"];

    public static string Header => string.Join(',', Columns);

    private static readonly Dictionary<string, DecisionKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pair"] = DecisionKind.Pair,
        ["not_pair"] = DecisionKind.NotPair,
        ["single"] = DecisionKind.Single,
    };

    private PairingDecisions(string path, List<PairingDecision> decisions)
    {
        Path = path;
        All = decisions;
    }

    public string Path { get; }

    public IReadOnlyList<PairingDecision> All { get; }

    /// <summary>
    /// 讀檔並檢查格式。寫壞的地方一次全部列出(點名行號),擲 <see cref="ConfigException"/>(exit 2)——
    /// 在寫任何輸出之前:決定檔讀錯一半就照樣跑,等於把人做過的決定安靜地丟掉一部分。
    /// </summary>
    public static PairingDecisions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException(
                $"pairing.decisions file not found: {path}\n  Create it with this header line: {Header}");
        }
        try
        {
            // Excel 的「CSV(逗號分隔)」存的是系統編碼(繁中 Windows 是 Big5),中文 key 會變成亂碼而對不上任何一頁。
            // 先嚴格驗 UTF-8,錯了講清楚怎麼存,不要讓每一列都變成「not in this run's inventory」
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException)
        {
            throw new ConfigException(
                $"{path} is not UTF-8. In Excel use Save As → \"CSV UTF-8 (Comma delimited)\".");
        }

        var rows = Csv.ReadRows(path).ToList();
        if (rows.Count == 0
            || !rows[0].Select(f => f.Trim()).Take(Columns.Length).SequenceEqual(Columns)
            || rows[0].Skip(Columns.Length).Any(f => f.Trim().Length > 0))
        {
            throw new ConfigException($"{path}: the first line must be the header: {Header}");
        }

        var errors = new List<string>();
        var decisions = new List<PairingDecision>();
        for (var i = 1; i < rows.Count; i++)
        {
            var line = i + 1;
            var f = rows[i].Select(Unguard).Concat(Enumerable.Repeat("", Columns.Length)).Take(Columns.Length).ToList();
            if (f.All(v => v.Length == 0))
            {
                continue;   // Excel 會在檔尾留 ",,," 這種空列
            }
            if (rows[i].Skip(Columns.Length).Any(v => v.Trim().Length > 0))
            {
                errors.Add($"line {line}: more than {Columns.Length} columns (a note containing a comma must be quoted)");
                continue;
            }
            var (key, decision, other, note) = (f[0], f[1], f[2], f[3]);
            if (key.Length == 0)
            {
                errors.Add($"line {line}: translation_key is empty");
                continue;
            }
            if (!Kinds.TryGetValue(decision, out var kind))
            {
                errors.Add($"line {line}: decision '{decision}' is not one of pair, not_pair, single");
                continue;
            }
            if (kind == DecisionKind.Single && other.Length > 0)
            {
                errors.Add($"line {line}: 'single' takes no other_key (did you mean 'pair'?)");
                continue;
            }
            if (kind != DecisionKind.Single && other.Length == 0)
            {
                errors.Add($"line {line}: '{decision.ToLowerInvariant()}' needs other_key");
                continue;
            }
            if (key == other)
            {
                errors.Add($"line {line}: translation_key and other_key are the same");
                continue;
            }
            decisions.Add(new PairingDecision(line, key, kind, other.Length == 0 ? null : other, note));
        }

        // 一個 key 只能有一個結論(配給誰,或確定沒有對應);否決可以有很多筆
        var settled = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in decisions.Where(d => d.Kind != DecisionKind.NotPair))
        {
            foreach (var k in d.Other is null ? [d.Key] : new[] { d.Key, d.Other! })
            {
                if (!settled.TryAdd(k, d.Line))
                {
                    errors.Add($"line {d.Line}: {k} is already decided on line {settled[k]}");
                }
            }
        }
        var pairs = decisions.Where(d => d.Kind == DecisionKind.Pair).ToDictionary(d => Ordered(d.Key, d.Other!), d => d.Line);
        foreach (var d in decisions.Where(d => d.Kind == DecisionKind.NotPair))
        {
            if (pairs.TryGetValue(Ordered(d.Key, d.Other!), out var pairLine))
            {
                errors.Add($"line {d.Line}: contradicts line {pairLine} ('pair' for the same two keys)");
            }
        }

        if (errors.Count > 0)
        {
            throw new ConfigException($"{path}: invalid pairing decisions —\n  " + string.Join("\n  ", errors));
        }
        return new PairingDecisions(path, decisions);
    }

    /// <summary>
    /// 對這次的 inventory 套用。套不上的不是錯(舊站改了、頁下架了都會發生),但要回報——
    /// 人做過的決定沒生效,不能安靜地略過。
    /// </summary>
    /// <param name="state">key → 這次的狀態;null = 不在 inventory 裡。</param>
    /// <param name="multilingual">false = 單語站,配對不適用。</param>
    public DecisionOutcome Resolve(Func<string, DecisionKeyState?> state, bool multilingual)
    {
        var confirmed = new Dictionary<string, string>(StringComparer.Ordinal);
        var single = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new HashSet<(string, string)>();
        var notes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unapplied = new List<string>();

        foreach (var d in All)
        {
            var reason = multilingual ? WhyNot(d, state) : "the site has a single locale, so pairing does not apply";
            if (reason is not null)
            {
                unapplied.Add($"line {d.Line}: {reason}");
                continue;
            }
            switch (d.Kind)
            {
                case DecisionKind.Pair:
                    confirmed[d.Key] = d.Other!;
                    confirmed[d.Other!] = d.Key;
                    break;
                case DecisionKind.NotPair:
                    rejected.Add(Ordered(d.Key, d.Other!));
                    break;
                case DecisionKind.Single:
                    single.Add(d.Key);
                    break;
            }
            if (d.Note.Length > 0)
            {
                foreach (var k in d.Other is null ? [d.Key] : new[] { d.Key, d.Other! })
                {
                    var list = notes.TryGetValue(k, out var existing) ? existing : notes[k] = [];
                    if (!list.Contains(d.Note))
                    {
                        list.Add(d.Note);
                    }
                }
            }
        }
        return new DecisionOutcome(confirmed, single, rejected,
            notes.ToDictionary(kv => kv.Key, kv => string.Join("; ", kv.Value), StringComparer.Ordinal), unapplied);
    }

    private static string? WhyNot(PairingDecision d, Func<string, DecisionKeyState?> state)
    {
        foreach (var k in d.Other is null ? [d.Key] : new[] { d.Key, d.Other! })
        {
            switch (state(k))
            {
                case null:
                    return $"{k} is not in this run's inventory (removed from the old site, or mistyped)";
                case { SiteLevel: true }:
                    return $"{k} is a site-level page, which is never paired";
                case { Paired: true }:
                    return $"{k} is already paired by its path";
            }
        }
        if (d.Kind == DecisionKind.Pair && state(d.Key)!.Locale is { } locale && locale == state(d.Other!)!.Locale)
        {
            return $"{d.Key} and {d.Other} are both {locale}";
        }
        return null;
    }

    /// <summary>無序的一對(否決不分前後)。</summary>
    public static (string, string) Ordered(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>
    /// 去掉前後空白,以及從 content_inventory 複製過來時帶的公式防護單引號(<c>'-foo</c> → <c>-foo</c>,
    /// 見 <see cref="Csv.Write"/>)。只拿掉「單引號 + 公式字元」那一種,其他單引號照留。
    /// </summary>
    private static string Unguard(string field)
    {
        var v = field.Trim();
        return v.Length > 1 && v[0] == '\'' && "=+-@".Contains(v[1]) ? v[1..] : v;
    }
}
