using System.Text.RegularExpressions;
using PolyMigrate.Cli;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// README(中英文)展示的指令,必須與 <c>--help</c> 實際列出的指令完全一致。
///
/// <para>這不是潔癖。實際發生過兩次:2.2.0 開發期間 README 寫 <c>polymigrate slugs out/</c>
/// 而 <c>--help</c> 寫 <c>&lt;root&gt;</c>,那個不一致撐過了一整輪人工審視才被發現;
/// 同一次改動裡英文 README 加了 <c>slugs</c>、中文 README 沒加,同樣沒人當場看出來。
/// 文件與程式各自演化、靠人記得同步,遲早會走鐘——而走鐘的文件比沒有文件更傷,
/// 因為使用者會照著做然後卡住。</para>
///
/// <para>只比對「有哪些指令」,不比對說明文字:說明本來就該在兩邊用各自的語言講,
/// 逐字比對只會製造假警報,反而讓人把測試關掉。</para>
/// </summary>
public class ReadmeDriftTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>`--help` 裡的指令列:行首兩格 + "polymigrate " + 指令名。</summary>
    private static readonly Regex HelpCommand =
        new(@"^  polymigrate (?<name>[a-z][a-z-]*)\b", RegexOptions.Multiline);

    /// <summary>README 程式碼區塊裡的指令列:行首直接是 "polymigrate "。</summary>
    private static readonly Regex ReadmeCommand =
        new(@"^polymigrate (?<name>[a-z][a-z-]*)\b", RegexOptions.Multiline);

    private static async Task<string> HelpText()
    {
        var origOut = Console.Out;
        var so = new StringWriter();
        Console.SetOut(so);
        try
        {
            await Cli.RunAsync(["--help"]);
            return so.ToString();
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    private static SortedSet<string> Names(Regex pattern, string text) =>
        [.. pattern.Matches(text).Select(m => m.Groups["name"].Value)];

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.zh-Hant.md")]
    public async Task Readme_ShowsExactlyTheCommandsHelpLists(string readme)
    {
        var help = Names(HelpCommand, await HelpText());
        var shown = Names(ReadmeCommand, await File.ReadAllTextAsync(Path.Combine(RepoRoot, readme)));

        Assert.NotEmpty(help);
        Assert.True(
            help.SetEquals(shown),
            $"""
            {readme} 與 --help 的指令清單不一致。
              只在 --help:  {string.Join(", ", help.Except(shown))}
              只在 README:  {string.Join(", ", shown.Except(help))}
            新增指令時 README.md 與 README.zh-Hant.md 都要更新。
            """);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PolyMigrate.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        }
        return dir;
    }
}
