namespace PolyMigrate.Cli.Tests;

/// <summary>
/// <c>polymigrate verify</c> 的參數與結束路徑(CliTests 末段已有 0/1/2 的基本驗收,這裡補其餘分支)。
/// </summary>
public class VerifyCommandTests : CliTestBase
{
    /// <summary>站根 index.md(內建豁免孤島偵測),內文由呼叫端決定。</summary>
    private string WriteSite(string body)
    {
        Write("out/content/index.md",
            $"""
            ---
            source_url: https://example.invalid/index.php
            lang: zh-Hant
            slug: index
            translation_key: /index
            title: 首頁
            page_type: page
            ---

            {body}

            """);
        return PathOf("out");
    }

    [Fact]
    public async Task Cancelled_Returns130_WithoutWritingTheReport()
    {
        // 原本同步指令不看 token:第一次 Ctrl-C 沒作用、照常跑完回 0/1/2;第二次由 runtime 直接終止,
        // 結束碼由作業系統決定(Windows 0xC000013A),不是契約的 130
        var outDir = WriteSite("# 首頁");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var r = await RunWith(cts.Token, "verify", outDir);

        Assert.Equal(130, r.Exit);
        Assert.Equal("cancelled.\n", r.Err);
        Assert.False(File.Exists(PathOf("out/verify_report.csv")));
    }

    [Fact]
    public async Task NoContentDirectory_ExitsTwo()
    {
        // 指錯目錄(例如把 raw/ 當成輸出根目錄)必須失敗,不能是「0 頁、全部通過」
        Directory.CreateDirectory(PathOf("out"));

        var r = await Run("verify", PathOf("out"));

        Assert.Equal(2, r.Exit);
        Assert.Contains("  errors          : 1\n", r.Out);
        Assert.Contains($"  [error] : no_content content directory not found: {PathOf("out/content")}\n", r.Out);
    }

    [Fact]
    public async Task MediaDirectoryAbsent_SaysSkipped_AndDoesNotFail()
    {
        var outDir = WriteSite("![圖](/media/x.jpg)");

        var r = await Run("verify", outDir);

        Assert.Equal(0, r.Exit);
        Assert.Contains("  media checks    : skipped (media directory not found)\n", r.Out);
        Assert.DoesNotContain("media refs", r.Out);
    }

    [Theory]
    [InlineData(true, 0)]    // 圖在 --media 指定的目錄裡 → 乾淨
    [InlineData(false, 2)]   // 不在 → missing_media(error)
    public async Task MediaOverride_IsWhereMediaIsLookedUp(bool present, int expectedExit)
    {
        var outDir = WriteSite("![圖](/media/img/x.jpg)");
        Directory.CreateDirectory(PathOf("elsewhere"));
        if (present)
        {
            Write("elsewhere/img/x.jpg", "jpeg");
        }

        var r = await Run("verify", outDir, "--media", PathOf("elsewhere"));

        Assert.Equal(expectedExit, r.Exit);
        Assert.Contains("  media refs      : 1\n", r.Out);
        if (!present)
        {
            Assert.Contains("  [error] index.md: missing_media /media/img/x.jpg\n", r.Out);
        }
    }

    [Fact]
    public async Task MediaPrefixOverride_ChangesWhatCountsAsMedia()
    {
        // 新站把媒體放在 /assets/ 時:/assets/ 的引用對磁碟查,/media/ 的就只是一般連結(→ 壞連結)
        var outDir = WriteSite("![a](/assets/a.jpg) [b](/media/b.jpg)");
        Write("out/media/a.jpg", "jpeg");

        var r = await Run("verify", outDir, "--media-prefix", "/assets/");

        Assert.Equal(2, r.Exit);
        Assert.Contains("  media refs      : 1\n", r.Out);
        Assert.Contains("  links checked   : 1\n", r.Out);
        Assert.Contains("  [error] index.md: broken_link /media/b.jpg\n", r.Out);
        Assert.DoesNotContain("missing_media", r.Out);
    }

    [Fact]
    public async Task ErrorsAndWarnings_ExitsTwo()
    {
        // error 優先於 warning:同時有兩者時是 2,不是 1
        var outDir = WriteSite("[壞](/nope) ![已知壞圖](/media/gone.jpg)");
        Directory.CreateDirectory(PathOf("out/media"));
        Write("out/missing_images.csv", "source_page,missing_image\r\nx,/media/gone.jpg\r\n");

        var r = await Run("verify", outDir);

        Assert.Equal(2, r.Exit);
        Assert.Contains("  errors          : 1\n", r.Out);
        Assert.Contains("  warnings        : 1\n", r.Out);
        Assert.Contains("  [warning] index.md: known_missing_media /media/gone.jpg\n", r.Out);
    }

    [Fact]
    public async Task WarningsOtherThanUnlinked_GetNoUnlinkedHint_AndNoErrorLines()
    {
        // 「去選單加連結、或用 --allow-unlinked」的提示只屬於 unlinked_page:原站壞圖這類 warning
        // 跟選單無關,印出來只會誤導。warning 也只出現在 [warning] 明細,不可混進 [error]
        var outDir = WriteSite("![已知壞圖](/media/gone.jpg)");
        Directory.CreateDirectory(PathOf("out/media"));
        Write("out/missing_images.csv", "source_page,missing_image\r\nx,/media/gone.jpg\r\n");

        var r = await Run("verify", outDir);

        Assert.Equal(1, r.Exit);
        Assert.Contains("  [warning] index.md: known_missing_media /media/gone.jpg\n", r.Out);
        Assert.DoesNotContain("[error]", r.Out);
        Assert.DoesNotContain("unlinked pages can only be reached", r.Out);
    }

    [Fact]
    public async Task ReportPath_IsPrinted_AndTheFileExists()
    {
        var outDir = WriteSite("沒有引用");

        var r = await Run("verify", outDir);

        var report = Path.Combine(outDir, "verify_report.csv");
        Assert.Contains($"  report          : {report}\n", r.Out);
        Assert.True(File.Exists(report));
    }

    [Fact]
    public async Task AllowUnlinked_EntriesTrimmed_PathsAndRoutesBothAccepted()
    {
        WriteSite("# 首頁");
        foreach (var page in (string[])["a", "b"])
        {
            Write($"out/content/ch/promo/{page}.md",
                $"---\nsource_url: x\nlang: zh-Hant\nslug: {page}\ntranslation_key: promo/{page}\ntitle: t\npage_type: page\n---\n\n內文\n");
        }
        var allow = Write("allow.txt", "\n# 活動到達頁\n   \n/ch/promo/a\n  ch/promo/b.md  \n");

        var r = await Run("verify", PathOf("out"), "--allow-unlinked", allow);

        Assert.Equal(0, r.Exit);
        Assert.Contains("  warnings        : 0\n", r.Out);
    }

    [Theory]
    [InlineData("--media")]
    [InlineData("--media-prefix")]
    [InlineData("--allow-unlinked")]
    public async Task OptionWithoutValue_ExitsTwo(string option)
    {
        // verify 的解析不吃「下一個是旗標」的值:缺值時該旗標本身被當成未知參數
        var r = await Run("verify", "out", option);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"Unexpected argument: {option}\n", r.Err);
    }

    [Fact]
    public async Task SecondOutputDir_ExitsTwo()
    {
        var r = await Run("verify", "out1", "out2");

        Assert.Equal(2, r.Exit);
        Assert.Equal("Unexpected argument: out2\n", r.Err);
    }
}
