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
    // 參數錯誤時印的 Usage 與 --help 的指令列必須是同一份:原本 extract 的 Usage 少了 --dry-run,
    // verify 與 probe-orphans 也是各自抄一份(文件與程式各自演化一定會走鐘,見 CLAUDE.md「已知教訓」)
    [InlineData("extract")]
    [InlineData("thumbs")]
    [InlineData("probe-orphans", "site.yaml")]
    [InlineData("fetch-orphans", "site.yaml")]
    [InlineData("verify")]
    [InlineData("slugs")]
    public async Task UsageOnError_IsTheSameSynopsisAsHelp(string command, string? arg = null)
    {
        var help = (await Run("--help")).Out.Split('\n');
        var start = Array.FindIndex(help, l => l.StartsWith($"  polymigrate {command} ", StringComparison.Ordinal));
        Assert.True(start >= 0, $"--help 裡找不到 {command}");
        // 指令列加上縮排的續行(以 [ 開頭的那幾行)
        var synopsis = string.Join(' ', help.Skip(start)
            .TakeWhile((l, i) => i == 0 || l.TrimStart().StartsWith('['))
            .Select(l => l.Trim()));

        var r = await Run(arg is null ? [command] : [command, arg]);

        Assert.Equal(2, r.Exit);
        var usage = r.Err.Split('\n').Single(l => l.StartsWith("Usage: ", StringComparison.Ordinal));
        Assert.Equal($"Usage: {synopsis}", usage);
    }

    [Fact]
    public async Task Help_StatesAliasesAndDefaults()
    {
        // 原本 --help 沒寫:-h / -v 別名、--years 可只給一年、probe 的 --lang 預設是 lang_map 第一個 key、
        // probe 的 --out 與 fetch 的 --slugs 預設 <root>/orphan_slugs.txt
        var help = (await Run("--help")).Out;

        Assert.Contains("polymigrate --help, -h", help);
        Assert.Contains("polymigrate --version, -v", help);
        Assert.Contains("--years: one year (2021) or a range (2021-2023)", help);
        Assert.Contains("--lang: defaults to the first url_pattern.lang_map key", help);
        Assert.Contains("--out: defaults to <root>/orphan_slugs.txt", help);
        Assert.Contains("--slugs: defaults to <root>/orphan_slugs.txt", help);
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
