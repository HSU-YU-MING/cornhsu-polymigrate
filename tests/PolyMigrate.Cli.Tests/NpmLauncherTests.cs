using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// npm 啟動腳本 <c>bin/polymigrate.js</c> 自己失敗時(工具根本沒有執行)的三條路徑。
///
/// <para>exit code 目前是 1,和 CLI 契約的「1 = 只有 warning」撞號——依「0/1 放行」的 CI 會把
/// 「工具根本沒跑」當成通過(2.1.0～2.3.0 的 arm64 事故走的就是這條路)。改成 2 屬既有情境的
/// exit code 改變,待維護者裁定;在那之前,至少訊息要用英文(與 CLI 其他輸出一致)講明
/// <b>PolyMigrate 沒有執行</b>,也講明這個 1 不是 warning。這裡釘住訊息,並把現行的 1 記下來——
/// 哪天裁定改成 2,這幾條測試會提醒要一起改 CHANGELOG 與 docs/contracts.md。</para>
///
/// <para>需要 PATH 上有 node(GitHub Actions 的三種 runner 都預裝)。</para>
/// </summary>
public class NpmLauncherTests : CliTestBase
{
    private static readonly string Launcher =
        Path.Combine(RepoRoot, "npm", "cornhsu-polymigrate", "bin", "polymigrate.js");

    private static async Task<(int Exit, string Out, string Err)> Node(
        IDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }
        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Win32Exception ex)
        {
            Assert.Fail($"node not found on PATH ({ex.Message}); the npm launcher tests need Node.js >= 18.");
            throw;
        }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, (await stdout).Replace("\r", ""), (await stderr).Replace("\r", ""));
        }
    }

    /// <summary>在 node 裡先把 <c>process.platform</c> 換掉再載入啟動腳本(它在載入時查平台表)。</summary>
    private static Task<(int Exit, string Out, string Err)> WithPlatform(string platform) =>
        Node(null, "-e",
            $"Object.defineProperty(process, 'platform', {{ value: '{platform}' }});"
            + $" process.argv = [process.argv[0], 'polymigrate', '--version']; require({Quote(Launcher)});");

    private static string Quote(string path) => "'" + path.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static void AssertLauncherFailure(string stderr)
    {
        Assert.DoesNotMatch(new Regex(@"\p{IsCJKUnifiedIdeographs}"), stderr);   // CLI 其他輸出都是英文
        Assert.StartsWith("error: ", stderr);
        Assert.Contains("PolyMigrate did not run.", stderr);
        Assert.Contains("exit code 1 comes from the npm launcher, not a PolyMigrate warning", stderr);
    }

    [Fact]
    public async Task UnsupportedPlatform_SaysItDidNotRun_InEnglish()
    {
        var (exit, stdout, stderr) = await WithPlatform("aix");

        Assert.Equal(1, exit);
        Assert.Empty(stdout);
        AssertLauncherFailure(stderr);
        Assert.Contains("no prebuilt binary for aix ", stderr);
        Assert.Contains("dotnet tool install -g Cornhsu.PolyMigrate", stderr);
    }

    [Fact]
    public async Task PlatformPackageMissing_SaysItDidNotRun_InEnglish()
    {
        // repo 裡沒有安裝任何平台包:require.resolve 找不到 → 這條路
        var (exit, stdout, stderr) = await Node(new Dictionary<string, string> { ["NODE_PATH"] = Root },
            Launcher, "--version");

        Assert.Equal(1, exit);
        Assert.Empty(stdout);
        AssertLauncherFailure(stderr);
        Assert.Contains("is not installed", stderr);
        Assert.Contains("npm install cornhsu-polymigrate", stderr);
    }

    [Fact]
    public async Task BinaryCannotBeStarted_SaysItDidNotRun_InEnglish()
    {
        // 平台包「裝了」但執行檔不在(安裝中斷的典型樣子):spawnSync 失敗 → 這條路。
        // 用 NODE_PATH 指到只有 package.json 的假平台包(六個平台都放,不必猜 node 的 process.arch)
        foreach (var key in new[] { "win32-x64", "win32-arm64", "linux-x64", "linux-arm64", "darwin-x64", "darwin-arm64" })
        {
            Write($"node_modules/@cornhsu/polymigrate-{key}/package.json", $"{{ \"name\": \"@cornhsu/polymigrate-{key}\" }}");
        }

        var (exit, stdout, stderr) = await Node(
            new Dictionary<string, string> { ["NODE_PATH"] = PathOf("node_modules") }, Launcher, "--version");

        Assert.Equal(1, exit);
        Assert.Empty(stdout);
        AssertLauncherFailure(stderr);
        Assert.Contains("could not start ", stderr);
    }
}
