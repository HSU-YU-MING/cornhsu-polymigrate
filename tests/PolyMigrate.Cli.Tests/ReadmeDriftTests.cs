using System.Text.RegularExpressions;
using PolyMigrate.Cli;
using PolyMigrate.Core.Configuration;

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
///
/// <para>README 的 YAML 設定範例也一樣:它必須真的載入得了。2.0.0 移除死欄位時,README 的範例還寫著
/// <c>pairing.strategy: symmetric_path</c>——<c>SiteConfigLoader</c> 對未知欄位報錯,照抄的人第一步就失敗;
/// 那次是人工在同一個 commit 補上的。3.0 起載入時又多驗一批欄位,而中文 README 自 2026-10-21 起有自己的一份範例,
/// 要同步的地方從一處變兩處。<c>examples/ibps-austin.yaml</c> 由 <c>SiteConfigLoaderTests.ExampleConfig_IsValid</c> 守著,
/// README 內嵌的這段原本沒有。</para>
/// </summary>
public class ReadmeDriftTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>README 的 YAML 設定範例:<c>```yaml</c> 到下一個 <c>```</c>。</summary>
    private static readonly Regex YamlBlock =
        new(@"^```yaml\n(?<body>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline);

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

    [Fact]
    public async Task PackagedReadme_HasNoRelativeLinks()
    {
        // README.md 隨 NuGet(PackageReadmeFile)與 npm 主套件出貨,nuget.org 會把相對連結清成 href="",
        // v2.3.1 套件頁上的 License 徽章、CHANGELOG、THIRD-PARTY-NOTICES、繁中說明都是死連結(稽核 L7;
        // 622edd4 已改成絕對網址)。這條擋住之後再貼回相對路徑。README.zh-Hant.md 不出貨,不在此限
        var text = await File.ReadAllTextAsync(Path.Combine(RepoRoot, "README.md"));
        var targets = Regex.Matches(text, @"\]\((?<t>[^)\s]+)")
            .Concat(Regex.Matches(text, @"\b(?:href|src)=""(?<t>[^""]*)"""))
            .Select(m => m.Groups["t"].Value);

        Assert.All(targets, t => Assert.True(
            t.StartsWith("https://", StringComparison.Ordinal) || t.StartsWith("http://", StringComparison.Ordinal)
            || t.StartsWith('#') || t.StartsWith("mailto:", StringComparison.Ordinal),
            $"README.md 有相對連結 '{t}'——nuget.org 套件頁上會變成死連結,請改成絕對網址"));
    }

    /// <summary>README 裡唯一的 YAML 設定範例(行尾正規化成 LF)。</summary>
    private static string ConfigSample(string readme)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, readme)).Replace("\r", "");
        var blocks = YamlBlock.Matches(text);
        // 恰好一個:README 改寫後抓不到區塊時要紅,而不是安靜地變成「零個範例通過」
        Assert.True(blocks.Count == 1,
            $"{readme} 應該恰好有一個 ```yaml 設定範例,實際找到 {blocks.Count} 個——README 改過的話跟著調整這條測試,不要刪掉它");
        return blocks[0].Groups["body"].Value;
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.zh-Hant.md")]
    public void Readme_ConfigSample_LoadsWithTheRealLoader(string readme)
    {
        var yaml = ConfigSample(readme);

        var error = Record.Exception(() => SiteConfigLoader.Load(yaml, readme));

        Assert.True(error is null,
            $"""
            {readme} 的 YAML 設定範例載入失敗:
              {error?.Message}
            這是使用者第一個照抄的 config。如果是 config 的改動讓它失敗(移除或改名欄位、收緊驗證),
            使用者手上同樣寫法的 config 也會一起失敗——那是 YAML 面的破壞性變更,要等 major(見 CLAUDE.md)。
            確定要改的話,README.md 與 README.zh-Hant.md 的範例一起改。
            """);
    }

    [Fact]
    public void Readme_ConfigSample_IsTheSameInBothLanguages()
    {
        // 註解各用各的語言;拿掉註解(行首,或空白之後的 #)與行尾空白後必須逐行相同。
        // 值裡的 # 前面不是空白(例如 "section[id]:not(#header)"),不會被當成註解
        static string Settings(string yaml) => string.Join("\n", yaml.Split('\n')
            .Select(line => Regex.Replace(line, @"(^|\s)#.*$", "").TrimEnd())
            .Where(line => line.Length > 0));

        var en = Settings(ConfigSample("README.md"));
        var zh = Settings(ConfigSample("README.zh-Hant.md"));

        Assert.True(en == zh,
            $"""
            兩份 README 的 YAML 設定範例不一樣(註解除外)。改了一份就要同步另一份。
            README.md:
            {en}
            README.zh-Hant.md:
            {zh}
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
