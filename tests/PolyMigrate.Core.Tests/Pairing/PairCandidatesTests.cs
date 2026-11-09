using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Inventory;
using PolyMigrate.Core.Pairing;

namespace PolyMigrate.Core.Tests.Pairing;

/// <summary>
/// <c>pair_candidates.csv</c>:每個還要覆核的 key 的前幾名候選與信心分級。content_inventory 的 suggested_pair
/// 是貪婪配對的結果、只有一筆——被別人先配走的更好對象、第二名差多少,原本都看不到。
/// </summary>
public class PairCandidatesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-candidates").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static KeyValuePair<string, string> E(string k, string v = "x") => new(k, v);

    [Theory]
    [InlineData("declared", "hreflang", "shared_media")]
    [InlineData("exact_slug", "slug_normalized", "title_similarity")]
    [InlineData("corroborated", "shared_media", "date")]
    [InlineData("corroborated", "date", "title_similarity")]
    [InlineData("single_signal", "shared_media")]
    [InlineData("single_signal", "date")]
    [InlineData("title_only", "title_similarity")]
    public void Confidence_IsNamedByWhichEvidenceAgrees_NotByScore(string expected, params string[] kinds)
    {
        Assert.Equal(expected, PairingSuggester.Confidence(kinds.Select(k => E(k)).ToList()));
    }

    private SiteConfig Config(string? decisions = null) => new()
    {
        Site = new SiteSection { BaseUrl = "https://old.example.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en" },
            DefaultLang = "zh-Hant",
            StripExtensions = [".php"],
        },
        Extract = new ExtractSection { Content = "main" },
        Pairing = new PairingSection { Fallback = ["date", "title_similarity"], Decisions = decisions },
    };

    private void AddRaw(string relative, string title)
    {
        var path = Path.Combine(_root, "raw", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<html><head><title>{title}</title></head><body><main>{relative}</main></body></html>");
    }

    private void Run(SiteConfig config) =>
        new ExtractionPipeline(config).Run(new ExtractionPaths(Path.Combine(_root, "raw"), Path.Combine(_root, "media"), _root));

    private List<string> Rows(string key) =>
        [.. Csv.ReadRows(Path.Combine(_root, "pair_candidates.csv")).Skip(1).Where(r => r[0] == key).Select(r => string.Join(',', r))];

    [Fact]
    public void AGreedyPickThatIsNotTheBestCandidate_IsVisible()
    {
        // 一篇中文、兩篇同一天的英文。貪婪配對照字典序走:photos 排最前面,它唯一的候選就是那篇中文(只有日期相同),
        // 於是先配走了;日期與標題都對得上的 event 只剩 missing。content_inventory 上只看得到「photos 配中文」,
        // 候選表把「第一名其實是 event」攤開
        AddRaw("ch/news/20260301.php.html", "Spring retreat");
        AddRaw("en/news/03012026-photos.php.html", "Photos");
        AddRaw("en/news/event-03012026.php.html", "Spring retreat");

        Run(Config());

        Assert.Equal(
        [
            "news/20260301,1,news/event-03012026,corroborated,date=2026-03-01;title_similarity=1.00,False",
            "news/20260301,2,news/03012026-photos,single_signal,date=2026-03-01,True",
        ], Rows("news/20260301"));
        Assert.Equal(["news/event-03012026,1,news/20260301,corroborated,date=2026-03-01;title_similarity=1.00,False"],
            Rows("news/event-03012026"));
    }

    [Fact]
    public void OnlyTheTopThreeAreListed_UnlessTheSuggestedOneRanksLower()
    {
        // 中文 a 有四個英文候選;第一名到第三名都被各自更像的中文頁先配走,a 拿到的是第四名——
        // 照實寫第 4 名,那正是「更好的對象被別人配走了」的訊號
        AddRaw("ch/news/20260301-a.php.html", "Retreat");
        for (var i = 1; i <= 3; i++)
        {
            AddRaw($"ch/news/0{i}-20260301.php.html", $"Retreat day {i}");
            AddRaw($"en/news/0{i}-03012026.php.html", $"Retreat day {i}");
        }
        AddRaw("en/news/zz-03012026.php.html", "Other");

        Run(Config());

        var rows = Rows("news/20260301-a");
        Assert.Equal(4, rows.Count);
        Assert.Equal(["1", "2", "3", "4"], rows.Select(r => r.Split(',')[1]));
        Assert.EndsWith(",True", rows[3]);
        Assert.All(rows.Take(3), r => Assert.EndsWith(",False", r));
    }

    [Fact]
    public void RejectedPairs_AndSettledKeys_AreNotCandidates()
    {
        AddRaw("ch/news/20260301.php.html", "Spring retreat");
        AddRaw("en/news/03012026-photos.php.html", "Photos");
        AddRaw("en/news/event-03012026.php.html", "Spring retreat");
        var decisions = Path.Combine(_root, "d.csv");
        File.WriteAllText(decisions,
            "translation_key,decision,other_key,note\r\nnews/20260301,not_pair,news/event-03012026,\r\nnews/03012026-photos,single,,\r\n");

        Run(Config(decisions));

        Assert.Empty(Rows("news/03012026-photos"));   // 已確認沒有對應,不進候選池
        Assert.Empty(Rows("news/20260301"));          // 剩下的唯一候選被否決了
        Assert.Empty(Rows("news/event-03012026"));
    }

    [Fact]
    public void ASingleLocaleSite_WritesTheHeaderOnly()
    {
        AddRaw("ch/news/a.php.html", "a");
        var config = Config();
        config.UrlPattern.LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant" };

        Run(config);

        Assert.Equal(["translation_key,rank,candidate,confidence,evidence,suggested"],
            File.ReadAllLines(Path.Combine(_root, "pair_candidates.csv")).Select(l => l.TrimStart('﻿')));
    }
}
