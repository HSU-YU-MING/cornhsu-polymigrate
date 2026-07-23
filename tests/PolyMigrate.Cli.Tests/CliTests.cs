using PolyMigrate.Cli;

// Console 是行程級全域,重導向不能並行 → 關掉這個組件的平行測試
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PolyMigrate.Cli.Tests;

public class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("polymigrate-cli").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var so = new StringWriter();
        var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var exit = await Cli.RunAsync(args);
            return (exit, so.ToString(), se.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task Version_ReturnsZero()
    {
        var r = await Run("--version");
        Assert.Equal(0, r.Exit);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task Help_ReturnsZero(string flag)
    {
        var r = await Run(flag);
        Assert.Equal(0, r.Exit);
        Assert.Contains("Usage:", r.Out);
    }

    [Fact]
    public async Task NoArgs_ShowsHelp_ReturnsZero()
    {
        var r = await Run();
        Assert.Equal(0, r.Exit);
        Assert.Contains("Usage:", r.Out);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsTwo()
    {
        var r = await Run("frobnicate");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Unknown command", r.Err);
    }

    // §3.8 契約 + 1.1.2 修正:選項值不得吞掉後面的旗標
    [Fact]
    public async Task Extract_OptionValueSwallowingFlag_IsRejected()
    {
        var r = await Run("extract", "site.yaml", "--root", "--dry-run");
        Assert.Equal(2, r.Exit);
        Assert.Contains("--root requires a value", r.Err);
    }

    [Fact]
    public async Task Extract_MissingConfig_ReturnsUsage()
    {
        var r = await Run("extract");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Usage: polymigrate extract", r.Err);
    }

    [Fact]
    public async Task Extract_NonexistentConfig_ReturnsTwo()
    {
        var r = await Run("extract", Path.Combine(_dir, "nope.yaml"));
        Assert.Equal(2, r.Exit);
        Assert.Contains("Config file not found", r.Err);
    }

    [Fact]
    public async Task Verify_MissingOutputDir_ReturnsUsage()
    {
        var r = await Run("verify");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Usage: polymigrate verify", r.Err);
    }

    [Fact]
    public async Task Verify_UnexpectedFlag_ReturnsTwo()
    {
        var r = await Run("verify", "out", "--bogus");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Unexpected argument", r.Err);
    }

    [Fact]
    public async Task Thumbs_MissingValueForOption_ReturnsTwo()
    {
        var r = await Run("thumbs", "site.yaml", "--media");
        Assert.Equal(2, r.Exit);
        Assert.Contains("--media requires a value", r.Err);
    }

    [Theory]
    [InlineData("2023-2021")]   // from > to
    [InlineData("abc")]         // not a number
    [InlineData("20-21-22")]    // too many parts
    public async Task ProbeOrphans_InvalidYears_ReturnsTwo(string years)
    {
        var r = await Run("probe-orphans", "site.yaml", "--section", "news", "--years", years);
        Assert.Equal(2, r.Exit);
        Assert.Contains("Invalid --years", r.Err);
    }

    [Fact]
    public async Task ProbeOrphans_MissingRequiredOption_ReturnsUsage()
    {
        // --years 缺失(--section 有):應回 usage
        var r = await Run("probe-orphans", "site.yaml", "--section", "news");
        Assert.Equal(2, r.Exit);
        Assert.Contains("Usage: polymigrate probe-orphans", r.Err);
    }
}
