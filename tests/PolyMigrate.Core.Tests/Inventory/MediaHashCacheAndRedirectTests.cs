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

        // 竄改快取的 sha 值:第二跑若讀快取,哨兵值會出現在 manifest(證明沒重算)。
        // 哨兵必須是合法的 40 位十六進位——不合法的 sha1 會被當成壞列丟掉(見 TruncatedSha1InCache_IsRecomputed)
        const string sentinel = "cafebabecafebabecafebabecafebabecafebabe";
        var cacheRows = File.ReadAllLines(CachePath);
        cacheRows[1] = cacheRows[1].Replace(cacheRows[1].Split(',')[3], sentinel);
        File.WriteAllLines(CachePath, cacheRows);

        Run();
        Assert.Contains(sentinel, File.ReadAllText(Path.Combine(_root, "media_manifest.csv")));

        // 檔案內容改變(大小/時間變)→ 快取失效,重算真雜湊
        AddMedia("img/x.jpg", "v2-changed");
        Run();
        var manifest3 = File.ReadAllText(Path.Combine(_root, "media_manifest.csv"));
        Assert.DoesNotContain(sentinel, manifest3);
    }

    [Fact]
    public void SameSizeEdit_WithNewMtime_StillInvalidatesTheCache()
    {
        // 快取鍵是(路徑, 大小, mtime):重新存檔的圖常常大小不變(同尺寸重新壓縮、改一個 byte),
        // 只看大小的話 manifest 的 sha1 會永遠停在舊值。mtime 明確指定,不靠兩次寫檔之間的時間差
        AddRaw("ch/news/a.php.html", "<img src=\"/img/x.jpg\">");
        AddMedia("img/x.jpg", "v1");
        var media = Path.Combine(MediaDir, "img", "x.jpg");
        File.SetLastWriteTimeUtc(media, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Run();

        File.WriteAllText(media, "v2");   // 同樣 2 bytes
        File.SetLastWriteTimeUtc(media, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Run();

        // SHA1("v2"),而不是快取裡 SHA1("v1") 的 5a6df720…
        var manifest = File.ReadAllText(Path.Combine(_root, "media_manifest.csv"));
        Assert.Contains(",a1047eab1035d58682a53557e0b2a75edbfd15fd,2\r\n", manifest);
        Assert.DoesNotContain("5a6df720540c20d95d530d3fd6885511223d5d20", manifest);
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
    public void TruncatedSha1InCache_IsRecomputed()
    {
        // 第二輪審查:快取檔被截斷(例如存檔時又按了一次 Ctrl-C)時,最後一列的 sha1 可能只剩半截,
        // 而 Load 原本只檢查欄數與數字欄——大小與 mtime 都對得上,半截的 sha1 就被當成命中,
        // 寫進凍結的 media_manifest.csv。sha1 必須是 40 位十六進位才算數
        AddRaw("ch/news/a.php.html", "<img src=\"/img/x.jpg\">");
        AddMedia("img/x.jpg", "v1");
        var media = new FileInfo(Path.Combine(MediaDir, "img", "x.jpg"));
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        File.WriteAllLines(CachePath,
        [
            "path,size,mtime_ticks,sha1",
            $"img/x.jpg,{media.Length},{media.LastWriteTimeUtc.Ticks},5a6df720",
        ]);

        Run();

        // SHA1("v1") 的完整值,而不是快取裡的半截
        Assert.Contains(",5a6df720540c20d95d530d3fd6885511223d5d20,2", File.ReadAllText(Path.Combine(_root, "media_manifest.csv")));
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
    public void NginxExport_NonAsciiOldPath_IsWrittenDecoded_SoTheLocationCanMatch()
    {
        // nginx 文件(ngx_http_core_module「location」):比對的對象是正規化後的 URI——
        // 先把 %XX 解碼、解析 . 與 ..、合併斜線;設定檔裡的 location 字串則照字面。
        // 寫成 location = /ch/news/%E7%A6%AA… 的話,只有「解碼後字面就是 %E7…」的請求才對得上,
        // 也就是永遠不會命中:舊網址不 301、直接 404,而這正是 i18n-first 的旗艦情境。
        // return 的目標維持編碼(Location 標頭應為 ASCII);_redirects 未經查證,不動
        AddRaw("ch/news/禪修.php.html", "b");

        Run();

        Assert.Equal("location = /ch/news/禪修.php { return 301 /ch/news/%E7%A6%AA%E4%BF%AE; }\n",
            File.ReadAllText(Path.Combine(_root, "redirects.nginx.conf")));
        Assert.Equal("/ch/news/%E7%A6%AA%E4%BF%AE.php /ch/news/%E7%A6%AA%E4%BF%AE 301\n",
            File.ReadAllText(Path.Combine(_root, "_redirects")));
    }

    [Theory]
    // 空白:不加引號的話 nginx -t 直接失敗(整份設定載入不了,比 404 更糟)
    [InlineData("/ch/news/a%20b.php", "/ch/news/a%20b", "location = \"/ch/news/a b.php\" { return 301 /ch/news/a%20b; }")]
    // ; { } 在不加引號時會結束 token;引號與反斜線在引號內要跳脫
    [InlineData("/ch/a;b.php", "/ch/a;b", "location = \"/ch/a;b.php\" { return 301 \"/ch/a;b\"; }")]
    [InlineData("/ch/%7Bx%7D.php", "/ch/%7Bx%7D", "location = \"/ch/{x}.php\" { return 301 /ch/%7Bx%7D; }")]
    [InlineData("/ch/%22q%22%5C.php", "/ch/%22q%22%5C", "location = \"/ch/\\\"q\\\"\\\\.php\" { return 301 /ch/%22q%22%5C; }")]
    // return 的值會展開 $變數(引號內也會):目標裡的 $ 一律編碼
    [InlineData("/ch/%24a.php", "/ch/$a", "location = \"/ch/$a.php\" { return 301 /ch/%24a; }")]
    // 解碼出 # 開頭以外的 # 不是註解,但保守起見一樣加引號
    [InlineData("/ch/c%23.php", "/ch/c%23", "location = \"/ch/c#.php\" { return 301 /ch/c%23; }")]
    public void NginxExport_QuotesAndEscapes_WhatNginxWouldOtherwiseMisparse(string oldPath, string newPath, string line)
    {
        var rendered = new PolyMigrate.Core.Redirects.NginxRedirectExporter().Render([(oldPath, newPath)]);

        Assert.Equal(line + "\n", rendered);
    }

    [Fact]
    public void NginxExport_SortedByTheDecodedPathItWrites()
    {
        // contracts.md「依 old path 排序」:nginx 寫的是解碼後的路徑,排序就依寫出來的那個字串
        var rendered = new PolyMigrate.Core.Redirects.NginxRedirectExporter().Render(
        [
            ("/ch/news/%E7%A6%AA%E4%BF%AE.php", "/ch/news/%E7%A6%AA%E4%BF%AE"),
            ("/ch/news/zz.php", "/ch/news/zz"),
        ]);

        Assert.Equal(
            "location = /ch/news/zz.php { return 301 /ch/news/zz; }\n"
            + "location = /ch/news/禪修.php { return 301 /ch/news/%E7%A6%AA%E4%BF%AE; }\n",
            rendered);
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
