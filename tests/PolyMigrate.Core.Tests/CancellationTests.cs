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
