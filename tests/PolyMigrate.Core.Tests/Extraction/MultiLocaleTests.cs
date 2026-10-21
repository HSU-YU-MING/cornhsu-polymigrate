using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>語言數不限中英:lang_map 幾組就幾語,inventory 欄位跟著展開。</summary>
public class MultiLocaleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-trilingual").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SiteConfig TrilingualConfig() => new()
    {
        Site = new SiteSection { BaseUrl = "https://example.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en", ["jp"] = "ja" },
            DefaultLang = "zh-Hant",
        },
        Extract = new ExtractSection { Content = "main" },
    };

    private void AddRaw(string relative)
    {
        var path = Path.Combine(_root, "raw", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"<html><head><title>{relative}</title></head><body><main>content of {relative}</main></body></html>");
    }

    [Fact]
    public void ThreeLocales_InventoryColumnsExpand_AndPairStatusWorks()
    {
        AddRaw("ch/news/a.php.html");
        AddRaw("en/news/a.php.html");
        AddRaw("jp/news/a.php.html");   // a:三語俱全 → paired
        AddRaw("ch/news/b.php.html");
        AddRaw("en/news/b.php.html");   // b:缺日文 → missing(部分配對,不硬猜)
        AddRaw("jp/news/c.php.html");   // c:只有日文 → missing

        var report = new ExtractionPipeline(TrilingualConfig())
            .Run(new ExtractionPaths(Path.Combine(_root, "raw"), Path.Combine(_root, "media"), _root));

        Assert.Equal(6, report.PagesWritten);
        Assert.Equal(1, report.OnlyInLocale["ja"]);

        var rows = File.ReadAllLines(Path.Combine(_root, "content_inventory.csv"));
        Assert.Contains("has_zh_hant", rows[0]);
        Assert.Contains("has_en", rows[0]);
        Assert.Contains("has_ja", rows[0]);
        Assert.Contains("text_len_ja", rows[0]);

        var a = rows.Single(r => r.StartsWith("news/a,"));
        var b = rows.Single(r => r.StartsWith("news/b,"));
        var c = rows.Single(r => r.StartsWith("news/c,"));
        Assert.Contains("paired", a);
        Assert.Contains("missing", b);
        Assert.Contains("missing", c);

        // 日文版 frontmatter 輸出 BCP-47 locale
        var jpMd = File.ReadAllText(Path.Combine(_root, "content", "jp", "news", "a.md"));
        Assert.Contains("lang: ja", jpMd);
    }

    [Fact]
    public void SingleLocaleSite_PairingColumnsLeftBlank()
    {
        // 單語站 = 只有一組對映的特例(§2.3):配對不適用,pair_status 留空(契約),
        // 而不是把每一頁都標成 paired——那會讓人以為工具替它找到了另一語言的版本
        AddRaw("ch/news/a.php.html");
        AddRaw("ch/news/b.php.html");
        AddRaw("index.php.html");       // 無前綴的站級頁,在多語站會是 site_level
        var config = TrilingualConfig();
        config.UrlPattern.LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant" };
        config.Pairing.Fallback = ["title_similarity"];

        var report = new ExtractionPipeline(config)
            .Run(new ExtractionPaths(Path.Combine(_root, "raw"), Path.Combine(_root, "media"), _root));

        Assert.Equal(3, report.PagesWritten);
        Assert.Equal(0, report.SuggestedPairs);
        Assert.Empty(report.OnlyInLocale);   // 「只有某語言」在單語站沒有意義

        var rows = File.ReadAllLines(Path.Combine(_root, "content_inventory.csv"))
            .Select(l => l.TrimStart('﻿').Split(','))
            .ToList();
        var header = rows[0];
        Assert.Equal(
            ["translation_key", "section", "slug", "has_zh_hant", "suggested_type", "final_type", "flags",
             "pair_status", "suggested_pair", "pair_evidence", "text_len_zh_hant", "image_count", "notes"],
            header);
        var pairStatus = Array.IndexOf(header, "pair_status");
        Assert.Equal(3, rows.Count - 1);
        Assert.All(rows.Skip(1), r => Assert.Equal("", r[pairStatus]));
    }
}
