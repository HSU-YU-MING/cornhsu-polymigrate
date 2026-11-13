using System.Globalization;
using System.Text.RegularExpressions;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// npm 通路的發版順序:平台子套件先發 → 等壓縮檔真的下載得到 → 入口套件最後發
/// (<c>scripts/publish-npm-packages.sh</c>)。
///
/// <para>為什麼不能一口氣發完:使用者裝的是入口套件,平台子套件是它的<b>選用</b>相依。入口套件先上架、
/// 子套件還沒同步好的那段時間,npm 不報錯、默默跳過,裝完一執行才 exit 2;npx 還會把那份半成品留在
/// <c>npm-cache/_npx/&lt;hash&gt;/</c>,registry 補齊之後它仍然是壞的。4.1.1 是平台包發完 <b>13 秒</b>
/// 就發入口套件,而同一個 registry 上 Parity 量到子套件最久要 <b>61 分鐘</b>才真的下載得到。</para>
///
/// <para>這裡釘住的是「接線」:腳本自己的行為另外驗,但 workflow 把步驟接錯、或有人改回
/// <c>npm publish</c>,是發版當下才會發現的那種錯。</para>
/// </summary>
public class NpmPublishOrderTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray())).ReplaceLineEndings("\n");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PolyMigrate.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("找不到 repo 根目錄");
    }

    [Fact]
    public void Release_publishes_platforms_then_waits_then_publishes_the_entry()
    {
        var release = Read(".github", "workflows", "release.yml");
        var positions = new[]
        {
            "node npm/prepare.mjs",
            "publish-npm-packages.sh platform npm/dist",
            "publish-npm-packages.sh wait npm/dist",
            "publish-npm-packages.sh entry npm/dist",
        }.Select(step => (step, at: release.IndexOf(step, StringComparison.Ordinal))).ToList();

        Assert.All(positions, p => Assert.True(p.at >= 0, $"release.yml 找不到 {p.step}"));
        Assert.Equal(positions.Select(p => p.at).Order(), positions.Select(p => p.at));
        // 回到「迴圈裡直接 npm publish」就等於回到空窗:子套件還沒同步,入口套件就上架了
        Assert.DoesNotContain("npm publish", release);
    }

    [Fact]
    public void The_step_timeout_is_longer_than_the_scripts_own_timeout()
    {
        // GitHub 先砍掉的話,看不到是哪幾個平台還沒好 —— 而那正是重跑前唯一需要知道的事
        var release = Read(".github", "workflows", "release.yml");
        var script = Read("scripts", "publish-npm-packages.sh");

        var own = int.Parse(Regex.Match(script, @"WAIT_TIMEOUT_MIN:-(?<min>\d+)").Groups["min"].Value, CultureInfo.InvariantCulture);
        var settle = int.Parse(Regex.Match(script, @"WAIT_SETTLE_SEC:-(?<sec>\d+)").Groups["sec"].Value, CultureInfo.InvariantCulture);
        var step = Regex.Match(release, @"timeout-minutes: (?<min>\d+)\n\s+run: bash scripts/publish-npm-packages\.sh wait");
        Assert.True(step.Success, "release.yml 的等待步驟要有 timeout-minutes");

        Assert.True(int.Parse(step.Groups["min"].Value, CultureInfo.InvariantCulture) > own + settle / 60);
    }

    [Fact]
    public void The_release_notes_are_extracted_before_anything_is_published()
    {
        // GitHub Release 的內容取自 CHANGELOG 對應那一節。抽取原本放在最後一步「Create GitHub Release」,
        // 抽不到就安靜退回一句 "See CHANGELOG.md."。問題是那一步跑到的時候套件已經推上 nuget.org、
        // 不可逆了 —— 在那裡失敗救不回任何東西,只會變成「套件出去了,但 Releases 頁是空的」。
        // 現在抽取搬進最前面那個 CHANGELOG 守門:抽不到就擋,此時刪 tag 重來還救得回來。
        var release = Read(".github", "workflows", "release.yml");

        var gate = release.IndexOf("Assert CHANGELOG top entry matches the tag", StringComparison.Ordinal);
        var extract = release.IndexOf("$RUNNER_TEMP/release-notes.md", StringComparison.Ordinal);
        var push = release.IndexOf("- name: Push to NuGet", StringComparison.Ordinal);
        var create = release.IndexOf("- name: Create GitHub Release", StringComparison.Ordinal);

        Assert.True(gate >= 0, "release.yml 找不到 CHANGELOG 守門");
        Assert.True(extract >= 0, "release.yml 沒有把 Release 內容抽出來存檔");
        Assert.True(gate < extract, "抽取要在守門那一步裡");
        Assert.True(extract < push, "抽取必須排在推上 nuget.org 之前,否則擋了也來不及");
        Assert.True(push < create, "建 Release 本來就該是最後一步");

        Assert.DoesNotContain("See CHANGELOG.md.", release);
        Assert.Contains("--notes-file", release);
        Assert.Single(Regex.Matches(release, "awk -v v="));   // 抽兩次遲早漂開
    }

    [Fact]
    public void The_token_guard_runs_before_anything_is_published()
    {
        // npm token 與 provenance 互斥:走 token 認證時憑證申請會 403(2.1.0 實際踩到)。
        // 守門擺在三個階段之前,失敗時一包都還沒發出去。
        var release = Read(".github", "workflows", "release.yml");

        var guard = release.IndexOf("Refuse to publish with an npm token", StringComparison.Ordinal);
        var first = release.IndexOf("publish-npm-packages.sh platform", StringComparison.Ordinal);

        Assert.True(guard >= 0, "release.yml 少了 npm token 守門");
        Assert.True(guard < first, "守門要排在第一個發佈階段之前");
    }
}
