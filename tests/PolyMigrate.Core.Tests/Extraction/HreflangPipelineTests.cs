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
    public void AlternateThatTheParserPutsInTheBody_IsStillRead()
    {
        // 刻意讀整份文件、不只 <head>(ReadAlternates 的註解與 docs 原本寫「<head> 裡的」,與實作不符;
        // 要不要改成只讀 <head> 待維護者裁定,見稽核 L3)。理由之一:舊 PHP 站的 include 檔常存成帶 BOM 的
        // UTF-8,BOM 字元出現在 <head> 中段,HTML5 解析器就提早開 <body>,之後的 <link> 全進了 body——
        // 只讀 doc.Head 的話,這類站的宣告會整批安靜地消失
        var html = "<html><head><title>a</title>" + (char)0xFEFF + Alternate("en", "/en/news/b.php")
            + "</head><body><main>中文</main></body></html>";
        Assert.Null(new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html).Head!.QuerySelector("link"));   // 防呆:確實落在 body
        var path = Path.Combine(_root, "raw", "ch", "news", "a.php.html");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, html);
        AddRaw("en/news/b.php.html");

        var report = Run(Config("hreflang"));

        Assert.Equal(1, report.HreflangDeclared);
        Assert.Equal(1, report.HreflangUsable);
    }

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
    public void AlternatesTouchingTheLanguageChooser_AreRejectedAsSiteLevel()
    {
        // 無前綴的語言選擇頁(translation_key 以 / 開頭)不是任何頁的翻譯:
        // 指向它、或由它宣告出去的 hreflang 都不可用於配對。
        // 斷言 CSV 裡的**字面字串**——reject_reason 是對外契約,HreflangIndexTests 比的是常數,
        // 常數值被改掉時那邊不會紅
        AddRaw("ch/news/a.php.html", Alternate("en", "/index.php"));
        AddRaw("index.php.html", Alternate("zh-Hant", "/ch/news/a.php"));

        var report = Run(Config("hreflang"));

        Assert.Equal(2, report.HreflangDeclared);
        Assert.Equal(0, report.HreflangUsable);
        var rows = HreflangRows();
        Assert.Contains("https://old.example.org/ch/news/a.php,en,https://old.example.org/index.php,/index,True,True,False,site_level", rows);
        Assert.Contains("https://old.example.org/index.php,zh-Hant,https://old.example.org/ch/news/a.php,news/a,True,True,False,site_level", rows);
    }

    [Fact]
    public void RejectReasonLiterals_InTheCsv_MatchTheContract()
    {
        // contracts.md 列的五個值,逐一以 CSV 字面值出現一次(golden 只涵蓋其中三個)
        AddRaw("ch/news/gone.php.html", Alternate("en", "/en/news/never-mirrored.php"));        // not_in_mirror
        AddRaw("ch/news/xd.php.html", Alternate("x-default", "/en/news/b.php"));               // x_default
        AddRaw("ch/news/self.php.html", Alternate("zh-Hant", "/ch/news/self.php"));            // self_reference
        AddRaw("ch/news/site.php.html", Alternate("en", "/index.php"));                        // site_level
        AddRaw("ch/news/t1.php.html", Alternate("en", "/en/news/b.php"));                      // ambiguous_target
        AddRaw("ch/news/t2.php.html", Alternate("en", "/en/news/b.php"));                      // ambiguous_target
        AddRaw("en/news/b.php.html");
        AddRaw("index.php.html");

        Run(Config("hreflang"));

        var reasons = HreflangRows().Skip(1).Select(r => r.Split(',')[^1]).ToHashSet();
        Assert.Equal(
            new HashSet<string> { "not_in_mirror", "x_default", "self_reference", "site_level", "ambiguous_target" },
            reasons);
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
