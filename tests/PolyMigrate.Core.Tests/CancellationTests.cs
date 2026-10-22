using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core.Tests;

/// <summary>
/// 同步指令的取消檢查點(M7):在「半途」取消,而不只是一開始就已取消的 token。
///
/// <para>審查 B2:原本只有「預先取消」的測試,而方法開頭多一個檢查會先把它接走——拿掉逐頁與寫檔前的
/// 檢查,測試照樣全綠。這裡靠 internal 的 <c>OnCheckpoint</c>(每個檢查點之前呼叫一次)在指定的
/// 檢查點取消,斷言停在那裡:之前的頁留著、之後的頁沒寫、清單或報告一份都沒寫、而且沒有再走到下一個檢查點。</para>
/// </summary>
public class CancellationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-cancel").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string P(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void WriteRaw(string relative, string body = "<p>內文</p>")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P("raw/" + relative))!);
        File.WriteAllText(P("raw/" + relative), $"<html><body><section id=\"personal\">{body}</section></body></html>");
    }

    /// <summary>跑到 <paramref name="cancelAt"/> 那個檢查點時取消;回傳走過的檢查點(含那一個)。</summary>
    private List<string> RunExtract(string cancelAt)
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        var pipeline = new ExtractionPipeline(TestConfigs.IbpsLike())
        {
            OnCheckpoint = label =>
            {
                seen.Add(label);
                if (label == cancelAt)
                {
                    cts.Cancel();
                }
            },
        };

        Assert.ThrowsAny<OperationCanceledException>(() => pipeline.Run(ExtractionPaths.ForRoot(_root), false, cts.Token));
        return seen;
    }

    [Fact]
    public void Extract_CancelledBeforeTheSecondPage_KeepsTheFirst_WritesNothingElse()
    {
        WriteRaw("ch/news/a.php.html");
        WriteRaw("ch/news/b.php.html");

        var seen = RunExtract("page:ch/news/b.php.html");

        Assert.Equal(["page:ch/news/a.php.html", "page:ch/news/b.php.html"], seen);
        Assert.True(File.Exists(P("content/ch/news/a.md")));      // 只寫不刪:已寫出的頁留著
        Assert.False(File.Exists(P("content/ch/news/b.md")));     // 取消之後不再寫新的頁
        Assert.False(File.Exists(P("content_inventory.csv")));    // 清單一份都不寫
    }

    [Fact]
    public void Extract_CancelledAfterTheLastPage_WritesNoInventories()
    {
        WriteRaw("ch/news/a.php.html");

        var seen = RunExtract("write");

        Assert.Equal("write", seen[^1]);
        Assert.True(File.Exists(P("content/ch/news/a.md")));
        Assert.Empty(Directory.GetFiles(_root, "*.csv"));
        Assert.False(File.Exists(P("need_fetch_media.txt")));
    }

    [Fact]
    public void Extract_CancelledWhileHashingMedia_StopsBeforeTheNextFile_WritesNoInventories()
    {
        // 審查 minor 1:最後一頁之後還有 media_manifest 的 sha1 計算,大媒體目錄可能要 30 秒以上;
        // 原本那段期間沒有檢查點,Ctrl-C 被吞掉、照常回 0/1。現在逐檔檢查,而且算完才開始寫清單
        foreach (var name in (string[])["a", "b"])
        {
            WriteRaw($"ch/news/{name}.php.html", $"<p>內文</p><img src=\"images/{name}.jpg\">");
            Directory.CreateDirectory(P("media/ch/news/images"));
            File.WriteAllText(P($"media/ch/news/images/{name}.jpg"), name);
        }

        var seen = RunExtract("media:ch/news/images/a.jpg");

        Assert.Equal("media:ch/news/images/a.jpg", seen[^1]);   // 沒有再去算 b.jpg
        Assert.Empty(Directory.GetFiles(_root, "*.csv"));
    }

    private void WriteTwoPagesWithMedia()
    {
        foreach (var name in (string[])["a", "b"])
        {
            WriteRaw($"ch/news/{name}.php.html", $"<p>內文</p><img src=\"images/{name}.jpg\">");
            Directory.CreateDirectory(P("media/ch/news/images"));
            File.WriteAllText(P($"media/ch/news/images/{name}.jpg"), name);
        }
    }

    [Fact]
    public void Extract_CancelledWhileHashingMedia_KeepsTheHashesAlreadyComputed()
    {
        // contracts.md:中斷時「已算好的媒體雜湊存進內部快取」——第二輪審查:拿掉那個 Save() 測試照樣全綠
        WriteTwoPagesWithMedia();

        RunExtract("media:ch/news/images/b.jpg");   // a.jpg 算完了、b.jpg 還沒

        var cache = File.ReadAllLines(P(".polymigrate/media_sha1_cache.csv"));
        Assert.Equal(2, cache.Length);   // 表頭 + a.jpg
        Assert.StartsWith("ch/news/images/a.jpg,", cache[1]);
        Assert.Empty(Directory.GetFiles(P(".polymigrate"), "*.tmp"));   // 先寫暫存再改名,不留半截檔
    }

    [Fact]
    public void Extract_CancelledAtTheWriteCheckpoint_KeepsAllComputedHashes()
    {
        // 第三輪審查 minor 2:雜湊已全部算完、在「開始寫清單」那個檢查點取消時,原本快取不會存
        WriteTwoPagesWithMedia();

        RunExtract("write");

        Assert.Equal(3, File.ReadAllLines(P(".polymigrate/media_sha1_cache.csv")).Length);   // 表頭 + a + b
        Assert.Empty(Directory.GetFiles(_root, "*.csv"));
    }

    [Fact]
    public void Extract_CacheCannotBeSaved_OutputsAreStillComplete()
    {
        // 第三輪審查 minor 2:正常路徑的快取存檔原本夾在 media_manifest.csv 與 redirect_map.csv 之間、沒有保護——
        // 存不了就擲 IOException,清單只寫一半、exit 2。內部快取只是加速檔:最後才存,存不了不影響輸出
        WriteTwoPagesWithMedia();
        File.WriteAllText(P(".polymigrate"), "not a directory");

        var report = new ExtractionPipeline(TestConfigs.IbpsLike()).Run(ExtractionPaths.ForRoot(_root));

        Assert.Equal(2, report.PagesWritten);
        foreach (var name in (string[])["content_inventory.csv", "hreflang_map.csv", "media_manifest.csv",
                     "redirect_map.csv", "missing_images.csv", "path_issues.csv", "need_fetch_media.txt",
                     "redirects.nginx.conf", "_redirects"])
        {
            Assert.True(File.Exists(P(name)), name);
        }
    }

    [Fact]
    public void Extract_CacheIsWrittenThroughATempFile()
    {
        // 第三輪審查 C5:存快取要「先寫暫存檔再改名」(連按兩下 Ctrl-C 時才不會留下被截斷的快取檔)。
        // 暫存檔的位置被目錄佔住時存不成,快取檔就不該出現——直接寫入最終檔名的實作會照樣寫出來
        WriteTwoPagesWithMedia();
        Directory.CreateDirectory(P(".polymigrate/media_sha1_cache.csv.tmp"));

        new ExtractionPipeline(TestConfigs.IbpsLike()).Run(ExtractionPaths.ForRoot(_root));

        Assert.False(File.Exists(P(".polymigrate/media_sha1_cache.csv")));
        Assert.True(File.Exists(P("media_manifest.csv")));   // 輸出不受影響
    }

    [Fact]
    public void CacheSaveFails_TheExistingCacheFileIsKept()
    {
        // 最終驗證 C4:存不了的時候只清自己的暫存檔,不可以順手刪掉上一次存好的正式快取檔
        WriteTwoPagesWithMedia();
        new ExtractionPipeline(TestConfigs.IbpsLike()).Run(ExtractionPaths.ForRoot(_root));
        var saved = File.ReadAllText(P(".polymigrate/media_sha1_cache.csv"));
        File.WriteAllText(P("media/ch/news/images/a.jpg"), "changed");   // 讓快取有新東西要存
        Directory.CreateDirectory(P(".polymigrate/media_sha1_cache.csv.tmp"));   // 暫存檔寫不出去

        new ExtractionPipeline(TestConfigs.IbpsLike()).Run(ExtractionPaths.ForRoot(_root));

        Assert.Equal(saved, File.ReadAllText(P(".polymigrate/media_sha1_cache.csv")));
    }

    [Fact]
    public void CacheSaveFails_AfterTheTempFileWasWritten_TheTempFileIsRemoved()
    {
        // 最終驗證 C3:暫存檔寫好了、改名失敗(正式檔名被目錄佔住)時,要把寫好的暫存檔清掉
        WriteTwoPagesWithMedia();
        Directory.CreateDirectory(P(".polymigrate/media_sha1_cache.csv"));

        new ExtractionPipeline(TestConfigs.IbpsLike()).Run(ExtractionPaths.ForRoot(_root));

        Assert.False(File.Exists(P(".polymigrate/media_sha1_cache.csv.tmp")));
        Assert.True(File.Exists(P("media_manifest.csv")));
    }

    [Fact]
    public void Extract_CancelledWhileHashingMedia_CacheCannotBeSaved_StillCancelled()
    {
        // 存快取失敗(這裡用一個「檔案」佔住 .polymigrate 目錄)不可以把取消蓋掉:CLI 要回 130,不是 2
        WriteTwoPagesWithMedia();
        File.WriteAllText(P(".polymigrate"), "not a directory");

        RunExtract("media:ch/news/images/b.jpg");   // RunExtract 斷言擲的是 OperationCanceledException
    }

    [Fact]
    public void Extract_NoPages_AlreadyCancelled_WritesNoInventories()
    {
        Directory.CreateDirectory(P("raw"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new ExtractionPipeline(TestConfigs.IbpsLike()).Run(ExtractionPaths.ForRoot(_root), false, cts.Token));

        Assert.Empty(Directory.GetFiles(_root, "*.csv"));
    }

    private void WriteContent(string page)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P("out/content/" + page))!);
        File.WriteAllText(P("out/content/" + page),
            "---\nsource_url: x\nlang: zh-Hant\nslug: s\ntranslation_key: k\ntitle: t\npage_type: page\n---\n\n內文\n");
    }

    private List<string> RunVerify(string cancelAt)
    {
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        var verifier = new OutputVerifier
        {
            OnCheckpoint = label =>
            {
                seen.Add(label);
                if (label == cancelAt)
                {
                    cts.Cancel();
                }
            },
        };

        Assert.ThrowsAny<OperationCanceledException>(() => verifier.Run(P("out"), null, "/media/", null, cts.Token));
        return seen;
    }

    [Fact]
    public void Verify_CancelledMidway_StopsAtThatPage_WritesNoReport()
    {
        WriteContent("a.md");
        WriteContent("b.md");
        WriteContent("c.md");

        var seen = RunVerify("page:b.md");

        Assert.Equal(["page:a.md", "page:b.md"], seen);   // 沒有再往 c.md 走
        Assert.False(File.Exists(P("out/verify_report.csv")));
    }

    [Fact]
    public void Verify_CancelledBeforeWritingTheReport_WritesNoReport()
    {
        WriteContent("a.md");

        var seen = RunVerify("write");

        Assert.Equal("write", seen[^1]);
        Assert.False(File.Exists(P("out/verify_report.csv")));
    }
}
