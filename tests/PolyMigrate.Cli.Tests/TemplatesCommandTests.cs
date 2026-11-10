namespace PolyMigrate.Cli.Tests;

/// <summary><c>polymigrate templates</c> 經過 CLI 的結束路徑:0 全部抓得到、1 有頁抓不到、2 用法 / 鏡像不存在。</summary>
public class TemplatesCommandTests : CliTestBase
{
    [Fact]
    public async Task FixtureSite_GroupsTemplates_ExitsZero_AndLeavesContentAlone()
    {
        var config = CopyFixtureSite();

        var r = await Run("templates", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains("Templates done.", r.Out);
        Assert.Contains("  pages     : 13\n", r.Out);
        Assert.Contains("captures nothing on 0 page(s)", r.Out);
        Assert.Contains("[T1] 4 page(s), e.g. ch/news/20260214.php.html: content captures 88% of the text; suggested #main (4/4)", r.Out);
        Assert.True(File.Exists(PathOf("site/template_report.csv")));
        Assert.True(File.Exists(PathOf("site/template_pages.csv")));
        Assert.False(Directory.Exists(PathOf("site/content")));   // 只讀鏡像,不抽取
    }

    [Fact]
    public async Task PagesWhereExtractContentSelectsNothing_ExitOne()
    {
        var config = Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", "<html><body><div id=\"home\"><p>沒有 main 元素的首頁</p></div></body></html>");

        var r = await Run("templates", config, "--out", PathOf("reports"));

        Assert.Equal(1, r.Exit);
        Assert.Contains("captures nothing on 1 page(s)", r.Out);
        Assert.Contains("suggested #home (1/1)", r.Out);
        Assert.True(File.Exists(PathOf("reports/template_report.csv")));
    }

    [Fact]
    public async Task MissingMirror_ExitsTwo()
    {
        var config = Write("site/site.yaml", Config());

        var r = await Run("templates", config);

        Assert.Equal(2, r.Exit);
        Assert.Contains("Raw mirror directory not found", r.Err);
    }

    [Fact]
    public async Task NoConfig_PrintsUsage_ExitsTwo()
    {
        var r = await Run("templates");

        Assert.Equal(2, r.Exit);
        Assert.Contains("polymigrate templates <config.yaml>", r.Err);
    }
}
