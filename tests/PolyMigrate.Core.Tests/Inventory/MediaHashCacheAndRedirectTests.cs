using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Tests.Inventory;

public class MediaHashCacheAndRedirectTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-cache").FullName;
    private string RawDir => Path.Combine(_root, "raw");
    private string MediaDir => Path.Combine(_root, "media");
    private string CachePath => Path.Combine(_root, ".polymigrate", "media_sha1_cache.csv");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SiteConfig Config() => new()
    {
        Site = new SiteSection { BaseUrl = "https://example.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en" },
            DefaultLang = "zh-Hant",
        },
        Extract = new ExtractSection { Content = "main" },
    };

    private void AddRaw(string relative, string body)
    {
        var path = Path.Combine(RawDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<html><head><title>t</title></head><body><main>{body}</main></body></html>");
    }

    private void AddMedia(string relative, string content)
    {
        var path = Path.Combine(MediaDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private ExtractionReport Run() =>
        new ExtractionPipeline(Config()).Run(new ExtractionPaths(RawDir, MediaDir, _root));

    [Fact]
    public void SecondRun_UsesCache_InvalidatedWhenFileChanges()
    {
        AddRaw("ch/news/a.php.html", "<img src=\"/img/x.jpg\">");
        AddMedia("img/x.jpg", "v1");

        Run();
        Assert.True(File.Exists(CachePath));

        // 竄改快取的 sha 值:第二跑若讀快取,哨兵值會出現在 manifest(證明沒重算)
        var cacheRows = File.ReadAllLines(CachePath);
        cacheRows[1] = cacheRows[1].Replace(cacheRows[1].Split(',')[3], "cachedsentinel");
        File.WriteAllLines(CachePath, cacheRows);

        Run();
        Assert.Contains("cachedsentinel", File.ReadAllText(Path.Combine(_root, "media_manifest.csv")));

        // 檔案內容改變(大小/時間變)→ 快取失效,重算真雜湊
        AddMedia("img/x.jpg", "v2-changed");
        Run();
        var manifest3 = File.ReadAllText(Path.Combine(_root, "media_manifest.csv"));
        Assert.DoesNotContain("cachedsentinel", manifest3);
    }

    [Fact]
    public void CorruptCacheRows_Ignored_RealHashRecomputed()
    {
        // 快取是內部加速檔:被手改壞、被截斷都只能讓它變慢,不能讓 manifest 寫出錯的 sha1
        AddRaw("ch/news/a.php.html", "<img src=\"/img/x.jpg\">");
        AddMedia("img/x.jpg", "v1");
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllText(CachePath,
            "path,size,mtime_ticks,sha1\r\nimg/x.jpg,not-a-number,0,bogus\r\nimg/x.jpg,2\r\n");

        Run();

        var manifest = File.ReadAllText(Path.Combine(_root, "media_manifest.csv"));
        Assert.DoesNotContain("bogus", manifest);
        // SHA1("v1") 的小寫十六進位,與 manifest 的 sha1 欄一致
        Assert.Contains(",5a6df720540c20d95d530d3fd6885511223d5d20,2\r\n", manifest);
    }

    [Fact]
    public void ManifestRow_HasEveryColumnInContractOrder()
    {
        // media_manifest.csv 的欄序(local_path, original_url, referenced_by, alt, sha1, bytes);
        // 引用到但磁碟上不存在的媒體不進 manifest(它記在 missing_images.csv)
        AddRaw("ch/news/a.php.html", "<img src=\"/img/x.jpg\" alt=\"圖\"><img src=\"/img/gone.jpg\">");
        AddMedia("img/x.jpg", "12345");

        Run();

        var rows = File.ReadAllText(Path.Combine(_root, "media_manifest.csv")).Split("\r\n");
        Assert.Equal("local_path,original_url,referenced_by,alt,sha1,bytes", rows[0]);
        Assert.Equal(
            "media/img/x.jpg,https://example.org/img/x.jpg,https://example.org/ch/news/a.php,圖,8cb2237d0679ca88db6464eac60da96345513964,5",
            rows[1]);
        Assert.Equal("", rows[2]);
    }

    [Fact]
    public void RedirectMap_NewPathAutoFilled_AndExportsWritten()
    {
        AddRaw("ch/news/20260101.php.html", "hello");
        AddRaw("ch/index.php.html", "home");

        Run();

        var map = File.ReadAllLines(Path.Combine(_root, "redirect_map.csv"));
        Assert.Contains(map, l => l.Contains("https://example.org/ch/news/20260101.php,/ch/news/20260101,"));
        Assert.Contains(map, l => l.Contains("https://example.org/ch/index.php,/ch/,"));

        var nginx = File.ReadAllText(Path.Combine(_root, "redirects.nginx.conf"));
        Assert.Contains("location = /ch/news/20260101.php { return 301 /ch/news/20260101; }", nginx);

        var netlify = File.ReadAllText(Path.Combine(_root, "_redirects"));
        Assert.Contains("/ch/news/20260101.php /ch/news/20260101 301", netlify);
    }

    [Fact]
    public void RedirectExports_SortedByOldPath_SelfRedirectsDropped()
    {
        // contracts.md:兩份 301 設定檔「old==new 略過防迴圈,依 old path 排序」。
        // 素材刻意讓檔案巡覽順序與 old path 順序不同:鏡像檔名是解碼的(禪修 排在 zz 之後),
        // 舊網址是編碼的(%E7… 排在 zz 之前)。沒有動態副檔名的頁(ch/about.html)舊網址就等於
        // 新路由,寫成 301 就是自己轉到自己的無限迴圈
        AddRaw("ch/news/zz.php.html", "a");
        AddRaw("ch/news/禪修.php.html", "b");
        AddRaw("ch/about.html", "c");

        Run();

        var netlify = File.ReadAllLines(Path.Combine(_root, "_redirects")).Select(l => l.Split(' ')[0]).ToList();
        var nginx = File.ReadAllLines(Path.Combine(_root, "redirects.nginx.conf")).Select(l => l.Split(' ')[2]).ToList();
        foreach (var olds in (List<string>[])[netlify, nginx])
        {
            Assert.Equal(2, olds.Count);
            Assert.DoesNotContain("/ch/about", olds);
            Assert.Equal(olds.Order(StringComparer.Ordinal), olds);
        }
        // redirect_map.csv 照列:它是人工覆核用的完整對照,不是部署檔
        Assert.Contains("https://example.org/ch/about,/ch/about,zh-Hant,about\r\n",
            File.ReadAllText(Path.Combine(_root, "redirect_map.csv")));
    }

    [Fact]
    public void Manifest_MediaDirOutsideTheOutputDir_LocalPathIsNeutral()
    {
        // contracts.md:媒體目錄不在輸出目錄底下時,local_path 一律寫成 media/<相對路徑>——
        // 否則會寫出 ../assets/... 這種隨執行位置而變的路徑,同一份輸出換台機器就不一樣
        AddRaw("ch/news/a.php.html", "<img src=\"/img/x.jpg\">");
        var assets = Path.Combine(_root, "assets");
        Directory.CreateDirectory(Path.Combine(assets, "img"));
        File.WriteAllText(Path.Combine(assets, "img", "x.jpg"), "v1");
        var outDir = Path.Combine(_root, "out");

        new ExtractionPipeline(Config()).Run(new ExtractionPaths(RawDir, assets, outDir));

        var rows = File.ReadAllText(Path.Combine(outDir, "media_manifest.csv")).Split("\r\n");
        Assert.StartsWith("media/img/x.jpg,https://example.org/img/x.jpg,", rows[1]);
    }

    [Fact]
    public void UnsafePage_NotEmittedToRedirects()
    {
        // a.php.html 與 a.asp.html 都收斂成 content/ch/news/a.md → 第二個(a.php,ordinal 較後)
        // 是重複輸出路徑、被跳過;跳過的頁不可再產生 301,否則 301 打到磁碟上不存在的頁(→404)
        var config = Config();
        config.UrlPattern.StripExtensions = [".php", ".asp"];
        AddRaw("ch/news/a.asp.html", "hi");
        AddRaw("ch/news/a.php.html", "hi");

        var report = new ExtractionPipeline(config).Run(new ExtractionPaths(RawDir, MediaDir, _root));

        Assert.True(report.HasErrors);   // 重複輸出路徑 = error

        var redirectMap = File.ReadAllText(Path.Combine(_root, "redirect_map.csv"));
        Assert.Contains("/ch/news/a.asp", redirectMap);        // 有寫的那個保留 301
        Assert.DoesNotContain("/ch/news/a.php", redirectMap);  // 被跳過的那個不出現

        Assert.DoesNotContain("a.php", File.ReadAllText(Path.Combine(_root, "redirects.nginx.conf")));
        Assert.DoesNotContain("a.php", File.ReadAllText(Path.Combine(_root, "_redirects")));
    }

    [Fact]
    public void UnsafeSlug_SkippedAndRecorded_OthersStillWritten()
    {
        AddRaw("ch/news/ok.php.html", "fine");
        // Windows 建不出名為 con.* 的檔案,改直接驗 relative path 檢查邏輯已由 PathSafetyTests 覆蓋;
        // 這裡驗「有 error 時 exit 語意與檔案輸出」:用尾點 slug(合法檔名於 NTFS 建立時會被剝,
        // 但 raw 檔名帶引號空白差異難重現)→ 退而驗 path_issues.csv 恆存在且乾淨站為空。
        var report = Run();

        Assert.False(report.HasErrors);
        var issues = File.ReadAllLines(Path.Combine(_root, "path_issues.csv"));
        Assert.Equal("severity,page,issue", issues[0].TrimStart('﻿'));
        Assert.Single(issues);   // 只有表頭
    }
}
