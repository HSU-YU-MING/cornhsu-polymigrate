using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>
/// 端到端:一個 hreflang 壞掉的站跑完整管線會怎樣。
///
/// <para>單元測試證明 <c>HreflangIndex</c> 的門檻對,這裡證明**那些門真的接上了管線**——
/// 來源語言要從 RawPage 一路傳到守門判斷,中間斷掉的話單元測試照樣全綠,
/// 而使用者拿到的是一筆掛著 hreflang 標籤的錯誤配對建議。</para>
/// </summary>
public class HreflangPipelineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-hreflang").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SiteConfig Config(params string[] fallback) => new()
    {
        Site = new SiteSection { BaseUrl = "https://old.example.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en" },
            DefaultLang = "zh-Hant",
            StripExtensions = [".php"],
        },
        Extract = new ExtractSection { Content = "main" },
        Pairing = new PairingSection { Fallback = [.. fallback] },
    };

    private void AddRaw(string relative, string headExtra = "")
    {
        var path = Path.Combine(_root, "raw", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"<html><head><title>{relative}</title>{headExtra}</head><body><main>body of {relative}</main></body></html>");
    }

    private static string Alternate(string hreflang, string href) =>
        $"<link rel=\"alternate\" hreflang=\"{hreflang}\" href=\"{href}\">";

    private ExtractionReport Run(SiteConfig config) =>
        new ExtractionPipeline(config).Run(
            new ExtractionPaths(Path.Combine(_root, "raw"), Path.Combine(_root, "media"), _root));

    private string[] HreflangRows() =>
        File.ReadAllLines(Path.Combine(_root, "hreflang_map.csv"));

    [Fact]
    public void TemplateCopiedAlternate_PointingEveryPageAtTheHomepage_ProducesNoSuggestion()
    {
        // SEO 外包把同一段 <link> 貼進每一頁的模板 —— 舊站最常見的壞法。
        // 這裡的三頁中文各自宣告英文首頁是自己的英文版,而一頁不可能是三頁的翻譯。
        foreach (var slug in (string[])["a", "b", "c"])
        {
            AddRaw($"ch/news/{slug}.php.html", Alternate("EN", "/en/home.php"));
        }
        AddRaw("en/home.php.html");

        var report = Run(Config("hreflang", "shared_media", "date", "title_similarity"));

        Assert.Equal(3, report.HreflangDeclared);
        Assert.Equal(0, report.HreflangUsable);      // 全部因 ambiguous_target 作廢
        Assert.Equal(0, report.SuggestedPairs);      // 沒有「英文首頁 = 某篇中文新聞」這種建議
        Assert.All(HreflangRows().Skip(1), r => Assert.Contains("ambiguous_target", r));
    }

    [Fact]
    public void RealPair_SurvivesInTheSameRun_AsTheBrokenOnes()
    {
        // 同一個站可以同時有壞掉的與好的宣告,壞的不該把好的一起拖下水
        foreach (var slug in (string[])["a", "b"])
        {
            AddRaw($"ch/news/{slug}.php.html", Alternate("en", "/en/home.php"));
        }
        AddRaw("en/home.php.html");
        AddRaw("ch/events/zh.php.html", Alternate("en", "/en/events/en.php"));
        AddRaw("en/events/en.php.html", Alternate("zh-Hant", "/ch/events/zh.php"));

        var report = Run(Config("hreflang"));

        Assert.Equal(4, report.HreflangDeclared);
        Assert.Equal(2, report.HreflangUsable);      // 只有 events 那一對過關
        Assert.Equal(1, report.SuggestedPairs);
        Assert.True(report.HreflangInFallback);
    }

    [Fact]
    public void HreflangNotInFallback_IsObservedButNotUsed()
    {
        // 讀還是照讀、CSV 照寫,但這一趟沒拿它配過任何一頁——摘要必須講得出這個差別
        AddRaw("ch/events/zh.php.html", Alternate("en", "/en/events/en.php"));
        AddRaw("en/events/en.php.html", Alternate("zh-Hant", "/ch/events/zh.php"));

        // 刻意不列 title_similarity:這兩頁的 fixture 標題本來就像,會蓋掉要測的東西
        var report = Run(Config("shared_media", "date"));

        Assert.Equal(2, report.HreflangDeclared);
        Assert.Equal(2, report.HreflangUsable);      // 查證結果與 config 無關
        Assert.False(report.HreflangInFallback);
        Assert.Equal(0, report.SuggestedPairs);      // 但完全沒作用
        Assert.DoesNotContain("hreflang=",
            File.ReadAllText(Path.Combine(_root, "content_inventory.csv")), StringComparison.Ordinal);
    }

    [Fact]
    public void SiteWithoutAnyHreflang_StillWritesTheFileWithHeaderOnly()
    {
        // 缺檔與空檔在診斷時意思完全不同:一個是「這版沒跑到」,一個是「這個站沒宣告」
        AddRaw("ch/news/a.php.html");
        AddRaw("en/news/a.php.html");

        var report = Run(Config("hreflang"));

        Assert.Equal(0, report.HreflangDeclared);
        var rows = HreflangRows();
        Assert.Single(rows);
        Assert.Contains("reject_reason", rows[0]);
    }
}
