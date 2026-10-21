namespace PolyMigrate.Cli.Tests;

/// <summary>
/// <c>polymigrate slugs</c> 的 CLI 路徑。Core 的 MirrorSlugsTests 證明清單本身對;
/// 這裡證明那些判斷真的接到 exit code 上——slugs 的輸出是給排程任務吃的,
/// 而排程任務只看 exit code 與 stdout。
/// </summary>
public class SlugsCommandTests : CliTestBase
{
    private void AddMirrored(string relativeDir, params string[] fileNames)
    {
        foreach (var name in fileNames)
        {
            Write($"{relativeDir}/{name}", "<html></html>");
        }
    }

    [Theory]
    // CLAUDE.md「已知教訓」:Path.Combine 後段是絕對路徑就丟掉前段 → 安靜地讀到鏡像外還回報成功。
    // 這幾筆在三個平台都必須被擋(判斷刻意不用 Path.IsPathRooted:Linux 上認不出 C:\)
    [InlineData("--section", "../x")]
    [InlineData("--section", "..")]
    [InlineData("--lang", "C:\\somewhere")]
    [InlineData("--lang", "/etc")]
    public async Task PathLikeSectionOrLang_ExitsTwo(string option, string value)
    {
        AddMirrored("raw/ch/news", "20260101.php.html");
        string[] args = option == "--section"
            ? ["slugs", Root, "--section", value]
            : ["slugs", Root, "--section", "news", "--lang", value];

        var r = await Run(args);

        Assert.Equal(2, r.Exit);
        Assert.Empty(r.Out);
        Assert.Contains("must be a single directory name", r.Err);
        Assert.StartsWith("error: ", r.Err);
    }

    [Fact]
    public async Task SectionPresentButEmpty_ExitsZero_EmptyStdout()
    {
        // 「有這個 section、但沒有頁」與「section 打錯」必須分得開:前者是合法的空清單
        Directory.CreateDirectory(PathOf("raw/ch/events"));

        var r = await Run("slugs", Root, "--section", "events");

        Assert.Equal(0, r.Exit);
        Assert.Empty(r.Out);
        Assert.Equal("0 slugs mirrored under events (all langs).\n", r.Err);
    }

    [Fact]
    public async Task RawOverride_ReadsThatDirectory()
    {
        AddMirrored("mirror/ch/news", "20260101.php.html");
        AddMirrored("raw/ch/news", "19990101.php.html");   // 預設位置的不該被讀到

        var r = await Run("slugs", Root, "--section", "news", "--raw", PathOf("mirror"));

        Assert.Equal(0, r.Exit);
        Assert.Equal("20260101\n", r.Out);
    }

    [Fact]
    public async Task JunkFiles_NeverReachStdout()
    {
        // .DS_Store 原本讓第一行變空行、Thumbs.db 變成假文章「Thumbs」——stdout 是要直接導成檔案的
        AddMirrored("raw/ch/news", "20260101.php.html", ".DS_Store", "Thumbs.db", "desktop.ini");

        var r = await Run("slugs", Root, "--section", "news", "--lang", "ch");

        Assert.Equal(0, r.Exit);
        Assert.Equal("20260101\n", r.Out);
        Assert.Equal("1 slugs mirrored under news (ch).\n", r.Err);
    }

    [Fact]
    public async Task Cancelled_Returns130_NothingOnStdout()
    {
        // 被中斷時 stdout 不可以留下半份清單:那會被導成檔案、當成完整的已鏡像清單去求差集
        AddMirrored("raw/ch/news", "20260101.php.html");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var r = await RunWith(cts.Token, "slugs", Root, "--section", "news");

        Assert.Equal(130, r.Exit);
        Assert.Equal("cancelled.\n", r.Err);
        Assert.Empty(r.Out);
    }

    [Fact]
    public async Task DottedSlug_MatchesTheSlugExtractWrites()
    {
        // 盤點 §1.3 的實測:raw/ch/news/v1.2-release.php.html → slugs 印 v1,extract 卻寫出 slug: v1.2-release。
        // 把 v1 餵給 fetch-orphans --slugs 會去抓 /ch/news/v1.php
        var config = Write("site.yaml", Config());
        Write("raw/ch/news/v1.2-release.php.html", Page("<p>release notes</p>"));

        var slugs = await Run("slugs", Root, "--section", "news");
        var extract = await Run("extract", config);

        Assert.Equal(0, slugs.Exit);
        Assert.Equal("v1.2-release\n", slugs.Out);
        Assert.Equal(0, extract.Exit);
        Assert.Contains("\nslug: v1.2-release\n", File.ReadAllText(PathOf("content/ch/news/v1.2-release.md")).Replace("\r", ""));
    }

    [Fact]
    public async Task UnknownLang_ExitsTwo_NamingTheLang()
    {
        AddMirrored("raw/ch/news", "20260101.php.html");

        var r = await Run("slugs", Root, "--section", "news", "--lang", "de");

        Assert.Equal(2, r.Exit);
        Assert.Equal($"No mirrored 'news' directory under {PathOf("raw")} for lang 'de'.\n", r.Err);
    }

    [Theory]
    [InlineData("--section")]
    [InlineData("--lang")]
    [InlineData("--raw")]
    public async Task OptionWithoutValue_ExitsTwo(string option)
    {
        var r = await Run("slugs", Root, "--section", "news", option);

        Assert.Equal(2, r.Exit);
        Assert.StartsWith($"Unexpected argument: {option}\nUsage: polymigrate slugs", r.Err);
    }

    [Fact]
    public async Task MissingRoot_ExitsTwo_WithUsage()
    {
        var r = await Run("slugs", "--section", "news");

        Assert.Equal(2, r.Exit);
        Assert.Equal("Usage: polymigrate slugs <root> --section <name> [--lang <prefix>] [--raw <dir>]\n", r.Err);
    }

    [Fact]
    public async Task SecondRoot_ExitsTwo()
    {
        var r = await Run("slugs", Root, "other", "--section", "news");

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("Unexpected argument: other\n", r.Err);
    }
}
