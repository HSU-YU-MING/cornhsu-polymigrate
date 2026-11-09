using System.Text;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Pairing;

namespace PolyMigrate.Core.Tests.Pairing;

/// <summary>
/// 人工配對決定檔(<c>pairing.decisions</c>)。<c>content_inventory.csv</c> 每次 extract 都整份重寫,
/// 人做的配對決定原本沒有地方放;這份檔案 extract 每次都讀、從不寫,所以重跑會保留。
/// </summary>
public class PairingDecisionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-decisions").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly Encoding Utf8Bom = new UTF8Encoding(true);

    private string Decisions(params string[] rows)
    {
        var path = Path.Combine(_root, "pairing_decisions.csv");
        File.WriteAllText(path, string.Join("\r\n", ["translation_key,decision,other_key,note", .. rows]) + "\r\n", Utf8Bom);
        return path;
    }

    private static string Load_Error(string path) =>
        Assert.Throws<ConfigException>(() => PairingDecisions.Load(path)).Message;

    // ── 讀檔與格式 ──────────────────────────────────────────

    [Fact]
    public void Load_ReadsTheThreeKindsOfDecision_IgnoringCaseBlankRowsAndTheFormulaGuardQuote()
    {
        var d = PairingDecisions.Load(Decisions(
            "news/a,pair,news/b,same retreat",
            ",,,",                                   // Excel 在檔尾留的空列
            "news/c,NOT_PAIR,news/d,",
            "'-draft,single,,\"no English page, ever\""));   // 從 content_inventory 複製來的 '-draft

        Assert.Collection(d.All,
            x => Assert.Equal(new PairingDecision(2, "news/a", DecisionKind.Pair, "news/b", "same retreat"), x),
            x => Assert.Equal(new PairingDecision(4, "news/c", DecisionKind.NotPair, "news/d", ""), x),
            x => Assert.Equal(new PairingDecision(5, "-draft", DecisionKind.Single, null, "no English page, ever"), x));
    }

    [Fact]
    public void Load_MissingFile_SaysWhichHeaderToCreate()
    {
        var message = Load_Error(Path.Combine(_root, "nope.csv"));

        Assert.Contains("pairing.decisions file not found", message);
        Assert.Contains("translation_key,decision,other_key,note", message);
    }

    [Fact]
    public void Load_NotUtf8_SaysHowToSaveFromExcel()
    {
        // Excel 的「CSV(逗號分隔)」在繁中 Windows 存成 Big5:中文 key 讀成亂碼,每一列都會「不在 inventory」
        var path = Path.Combine(_root, "big5.csv");
        File.WriteAllBytes(path, [.. "translation_key,decision,other_key,note\r\nnews/"u8, 0xA4, 0xA4, .. ",single,,\r\n"u8]);

        Assert.Contains("CSV UTF-8", Load_Error(path));
    }

    [Fact]
    public void Load_WrongHeader_IsRefused()
    {
        var path = Path.Combine(_root, "h.csv");
        File.WriteAllText(path, "key,decision,other,note\r\n", Utf8Bom);

        Assert.Contains("the first line must be the header: translation_key,decision,other_key,note", Load_Error(path));
    }

    [Fact]
    public void Load_ListsEveryMistakeWithItsLine()
    {
        var message = Load_Error(Decisions(
            "news/a,maybe,news/b,",          // 2
            "news/a,pair,,",                 // 3
            "news/a,single,news/b,",         // 4
            "news/a,pair,news/a,",           // 5
            ",single,,",                     // 6
            "news/a,pair,news/b,x,extra"));  // 7

        Assert.Contains("line 2: decision 'maybe' is not one of pair, not_pair, single", message);
        Assert.Contains("line 3: 'pair' needs other_key", message);
        Assert.Contains("line 4: 'single' takes no other_key", message);
        Assert.Contains("line 5: translation_key and other_key are the same", message);
        Assert.Contains("line 6: translation_key is empty", message);
        Assert.Contains("line 7: more than 4 columns", message);
    }

    [Fact]
    public void Load_AKeyDecidedTwice_OrAPairBothConfirmedAndRejected_IsRefused()
    {
        var message = Load_Error(Decisions(
            "news/a,pair,news/b,",
            "news/c,pair,news/b,",
            "news/d,single,,",
            "news/d,single,,",
            "news/b,not_pair,news/a,",
            "news/e,not_pair,news/f,",
            "news/e,not_pair,news/g,"));     // 否決可以有很多筆

        Assert.Contains("line 3: news/b is already decided on line 2", message);
        Assert.Contains("line 5: news/d is already decided on line 4", message);
        Assert.Contains("line 6: contradicts line 2", message);
        Assert.DoesNotContain("line 7", message);
        Assert.DoesNotContain("line 8", message);
    }

    // ── 套用 ───────────────────────────────────────────────

    private static DecisionKeyState? State(string key) => key switch
    {
        "news/zh" => new(false, false, "zh-Hant"),
        "news/en" => new(false, false, "en"),
        "news/zh2" => new(false, false, "zh-Hant"),
        "news/both" => new(false, true, null),
        "/index" => new(true, false, null),
        _ => null,
    };

    [Fact]
    public void Resolve_ReportsEveryDecisionItCannotApply()
    {
        var d = PairingDecisions.Load(Decisions(
            "news/zh,pair,news/en,ok",
            "news/gone,single,,",
            "news/both,single,,",
            "/index,single,,",
            "news/zh2,pair,news/zh9,",
            "news/zh2,not_pair,news/both,"));

        var o = d.Resolve(State, multilingual: true);

        Assert.Equal("news/en", o.Confirmed["news/zh"]);
        Assert.Equal("news/zh", o.Confirmed["news/en"]);
        Assert.Equal("ok", o.Notes["news/en"]);
        Assert.Equal(
        [
            "line 3: news/gone is not in this run's inventory (removed from the old site, or mistyped)",
            "line 4: news/both is already paired by its path",
            "line 5: /index is a site-level page, which is never paired",
            "line 6: news/zh9 is not in this run's inventory (removed from the old site, or mistyped)",
            "line 7: news/both is already paired by its path",
        ], o.Unapplied);
    }

    [Fact]
    public void Resolve_APairInTheSameLocale_IsNotApplied()
    {
        var o = PairingDecisions.Load(Decisions("news/zh,pair,news/zh2,")).Resolve(State, multilingual: true);

        Assert.Empty(o.Confirmed);
        Assert.Equal(["line 2: news/zh and news/zh2 are both zh-Hant"], o.Unapplied);
    }

    [Fact]
    public void Resolve_OnASingleLocaleSite_AppliesNothingAndSaysWhy()
    {
        var o = PairingDecisions.Load(Decisions("news/zh,single,,")).Resolve(State, multilingual: false);

        Assert.Empty(o.Single);
        Assert.Equal(["line 2: the site has a single locale, so pairing does not apply"], o.Unapplied);
    }

    // ── 端到端:extract 管線 ──────────────────────────────────

    private SiteConfig Config(string? decisions) => new()
    {
        Site = new SiteSection { BaseUrl = "https://old.example.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en" },
            DefaultLang = "zh-Hant",
            StripExtensions = [".php"],
        },
        Extract = new ExtractSection { Content = "main" },
        Pairing = new PairingSection { Fallback = ["date"], Decisions = decisions },
    };

    private void AddRaw(string relative)
    {
        var path = Path.Combine(_root, "raw", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<html><head><title>{relative}</title></head><body><main>body of {relative}</main></body></html>");
    }

    private ExtractionReport Run(SiteConfig config) =>
        new ExtractionPipeline(config).Run(
            new ExtractionPaths(Path.Combine(_root, "raw"), Path.Combine(_root, "media"), _root));

    /// <summary>translation_key → (pair_status, suggested_pair, pair_evidence, notes)。</summary>
    private Dictionary<string, (string Status, string Pair, string Evidence, string Notes)> Inventory()
    {
        var rows = Inventory_Rows();
        var h = rows[0].ToList();
        return rows.Skip(1).ToDictionary(r => r[0], r => (
            r[h.IndexOf("pair_status")], r[h.IndexOf("suggested_pair")], r[h.IndexOf("pair_evidence")], r[h.IndexOf("notes")]));
    }

    private List<IReadOnlyList<string>> Inventory_Rows() =>
        [.. PolyMigrate.Core.Inventory.Csv.ReadRows(Path.Combine(_root, "content_inventory.csv"))];

    private void AddSite()
    {
        AddRaw("ch/news/20260301.php.html");          // 日期 2026-03-01,英文有兩頁同一天
        AddRaw("en/news/03012026-photos.php.html");
        AddRaw("en/news/event-03012026.php.html");
        AddRaw("ch/news/a.php.html");                 // 沒有任何證據:missing
        AddRaw("en/news/b.php.html");
        AddRaw("ch/news/notice.php.html");            // 確定沒有英文版
    }

    [Fact]
    public void WithoutDecisions_TheHeuristicPicksTheFirstCandidate()
    {
        AddSite();

        var report = Run(Config(null));

        var inv = Inventory();
        Assert.Equal(("heuristic_suggested", "news/03012026-photos", "date=2026-03-01", ""), inv["news/20260301"]);
        Assert.Equal("missing", inv["news/a"].Status);
        Assert.Equal("missing", inv["news/notice"].Status);
        Assert.Equal(0, report.ConfirmedPairs);
        Assert.Empty(report.UnappliedDecisions);
    }

    [Fact]
    public void Decisions_ConfirmRejectAndSettle_AndSurviveARerun()
    {
        AddSite();
        var decisions = Decisions(
            "news/20260301,not_pair,news/03012026-photos,photos are from another day",
            "news/a,pair,news/b,found by hand",
            "news/notice,single,,ch only");

        var first = Run(Config(decisions));
        var firstInventory = File.ReadAllBytes(Path.Combine(_root, "content_inventory.csv"));
        var second = Run(Config(decisions));

        // 否決之後改建議下一個候選,而不是乾脆不建議
        var inv = Inventory();
        Assert.Equal(("heuristic_suggested", "news/event-03012026", "date=2026-03-01", "photos are from another day"), inv["news/20260301"]);
        Assert.Equal(("missing", "", "", "photos are from another day"), inv["news/03012026-photos"]);
        Assert.Equal(("confirmed", "news/b", "decision=pair", "found by hand"), inv["news/a"]);
        Assert.Equal(("confirmed", "news/a", "decision=pair", "found by hand"), inv["news/b"]);
        Assert.Equal(("confirmed_single", "", "decision=single", "ch only"), inv["news/notice"]);
        Assert.Equal((1, 1, 1, 1), (second.ConfirmedPairs, second.ConfirmedSingle, second.RejectedPairs, second.SuggestedPairs));
        Assert.False(second.HasWarnings);
        Assert.Equal(firstInventory, File.ReadAllBytes(Path.Combine(_root, "content_inventory.csv")));   // 重跑逐位元相同
        Assert.Equal(first.ConfirmedPairs, second.ConfirmedPairs);
        Assert.Equal(4, File.ReadAllLines(decisions).Length);   // extract 從不寫決定檔
    }

    [Fact]
    public void ADecisionThatNoLongerApplies_IsAWarning_AndChangesNothing()
    {
        AddSite();
        var decisions = Decisions("news/removed,single,,");

        var report = Run(Config(decisions));

        Assert.Equal(["line 2: news/removed is not in this run's inventory (removed from the old site, or mistyped)"], report.UnappliedDecisions);
        Assert.True(report.HasWarnings);
        Assert.Equal("heuristic_suggested", Inventory()["news/20260301"].Status);
    }

    [Fact]
    public void ABrokenDecisionsFile_StopsBeforeAnythingIsWritten()
    {
        AddSite();

        Assert.Throws<ConfigException>(() => Run(Config(Decisions("news/a,maybe,news/b,"))));

        Assert.False(File.Exists(Path.Combine(_root, "content_inventory.csv")));
        Assert.False(Directory.Exists(Path.Combine(_root, "content")));
    }
}
