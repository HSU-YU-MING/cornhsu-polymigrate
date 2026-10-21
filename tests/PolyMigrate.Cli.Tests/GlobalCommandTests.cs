using System.Reflection;

namespace PolyMigrate.Cli.Tests;

/// <summary>全域旗標與指令派發:--version / -v、未知指令、子指令不吃 --help。</summary>
public class GlobalCommandTests : CliTestBase
{
    private static readonly string InformationalVersion = typeof(Cli).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public async Task Version_PrintsTheAssemblyInformationalVersion(string flag)
    {
        // --version 的輸出就是組件的 InformationalVersion(release.yml 以 -p:Version= 從 tag 注入,
        // 從原始碼建置則是 Directory.Build.props 的 0.0.0-dev)
        var r = await Run(flag);

        Assert.Equal(0, r.Exit);
        Assert.Equal(InformationalVersion + "\n", r.Out);
        Assert.Empty(r.Err);
    }

    [Fact]
    public void SourceBuild_NeverClaimsTheMsBuildDefaultVersion()
    {
        // CLAUDE.md「已知教訓」:<Version> 留空時退回 MSBuild 預設 1.0.0,而 1.0.0 是 PolyMigrate
        // 真的發行過的版本——執行檔自稱一個存在、但不是它自己的版本。1.0.0 已在 NuGet 上,
        // 不可能再發一次,所以任何建置自稱 1.0.0 都是版本欄位壞了
        Assert.False(InformationalVersion.StartsWith("1.0.0", StringComparison.Ordinal),
            $"InformationalVersion = {InformationalVersion}");
    }

    [Fact]
    public async Task Help_FirstLineCarriesTheSameVersion()
    {
        var r = await Run("--help");

        Assert.StartsWith($"polymigrate {InformationalVersion} — ", r.Out);
    }

    [Theory]
    [InlineData("help")]          // 不是別名:help 指令不存在
    [InlineData("--VERSION")]     // 旗標分大小寫
    [InlineData("Extract")]       // 指令名分大小寫
    public async Task NotACommand_ExitsTwo(string arg)
    {
        var r = await Run(arg);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"Unknown command: {arg}. Run 'polymigrate --help'.\n", r.Err);
        Assert.Empty(r.Out);
    }

    [Theory]
    // 子指令沒有自己的 --help:它被當成未知參數 → exit 2。改成 exit 0 印說明是可以的,
    // 但那是 CLI 契約的變更,要寫進 CHANGELOG——這組測試讓那個決定變得看得見
    [InlineData("extract")]
    [InlineData("thumbs")]
    [InlineData("probe-orphans")]
    [InlineData("fetch-orphans")]
    [InlineData("verify")]
    [InlineData("slugs")]
    public async Task SubcommandHelp_IsAnUnexpectedArgument_ExitsTwo(string command)
    {
        var r = await Run(command, "--help");

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("Unexpected argument: --help", r.Err);
        Assert.Empty(r.Out);
    }
}
