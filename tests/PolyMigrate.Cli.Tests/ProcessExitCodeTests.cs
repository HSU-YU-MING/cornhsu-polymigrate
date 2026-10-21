using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// exit code 契約的最後一哩:其餘測試都呼叫 <c>Cli.RunAsync</c> 看回傳值,但使用者(CI、shell 的 set -e)
/// 看到的是**行程**的結束碼——中間隔著 Program.cs。這裡真的起一個 polymigrate 行程,
/// 確認 0 / 1 / 2 原封不動地走到作業系統。
/// </summary>
public class ProcessExitCodeTests : CliTestBase
{
    /// <summary>測試輸出目錄裡的 polymigrate.dll(Cli 專案參考會一併複製過來)。</summary>
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "polymigrate.dll");

    /// <summary>執行中這個 runtime 所屬的 dotnet 主機(shared/Microsoft.NETCore.App/&lt;ver&gt;/ 往上三層)。</summary>
    private static string DotnetHost()
    {
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(host) ? host : "dotnet";
    }

    private static Task<(int Exit, string Out, string Err)> Spawn(params string[] args) => SpawnIn(null, args);

    /// <param name="workingDirectory">行程的目前目錄;null = 沿用測試行程的。</param>
    private static async Task<(int Exit, string Out, string Err)> SpawnIn(string? workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        psi.ArgumentList.Add(CliDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, (await stdout).Replace("\r", ""), (await stderr).Replace("\r", ""));
    }

    [Fact]
    public async Task Version_ExitsZero_ThroughTheRealProcess()
    {
        var (exit, stdout, _) = await Spawn("--version");

        Assert.Equal(0, exit);
        Assert.Equal((await Run("--version")).Out, stdout);
    }

    [Fact]
    public async Task WarningsOnly_ExitsOne_ThroughTheRealProcess()
    {
        var config = CopyFixtureSite();   // 2 張壞圖 + 1 份待補 PDF

        var (exit, stdout, _) = await Spawn("extract", config, "--dry-run");

        Assert.Equal(1, exit);
        Assert.Contains("Phase 2 dry-run done (nothing written).", stdout);
    }

    [Fact]
    public async Task BareConfigFileName_WithNoDirectoryPart_StillResolvesTheDefaultRoot()
    {
        // 最常見的用法是在站目錄裡打 `polymigrate extract site.yaml`:config 路徑沒有目錄部分。
        // 預設 root 必須從完整路徑取目錄,否則 Path.GetDirectoryName 拿到的是空字串——
        // 其餘測試都傳絕對路徑,測不到這一格。這裡目前目錄剛好就是 config 所在目錄,
        // 所以分不出「config 的目錄」與「目前目錄」,那一半見下一條。
        // 目前目錄是行程級狀態,所以在子行程裡跑,不動測試行程的
        Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var (exit, stdout, stderr) = await SpawnIn(PathOf("site"), "extract", "site.yaml");

        Assert.Equal(0, exit);
        Assert.Empty(stderr);
        Assert.Contains("  markdown files   : 1\n", stdout);
        Assert.True(File.Exists(PathOf("site/content/ch/index.md")));
    }

    [Fact]
    public async Task RelativeConfigPath_FromAnotherDirectory_DefaultRootIsTheConfigDirectory()
    {
        // 從上一層打 `polymigrate extract site/site.yaml`:目前目錄 ≠ config 所在目錄。
        // 預設 root 若誤用目前目錄,會去找 ./raw(不存在)而 exit 2,輸出也會落在錯的地方
        Write("site/site.yaml", Config());
        Write("site/raw/ch/index.php.html", Page("<p>首頁</p>"));

        var (exit, stdout, stderr) = await SpawnIn(Root, "extract", Path.Combine("site", "site.yaml"));

        Assert.Equal(0, exit);
        Assert.Empty(stderr);
        Assert.Contains("  markdown files   : 1\n", stdout);
        Assert.True(File.Exists(PathOf("site/content/ch/index.md")));
        Assert.False(Directory.Exists(PathOf("content")));
    }

    [Fact]
    public async Task UsageError_ExitsTwo_ThroughTheRealProcess()
    {
        var (exit, _, stderr) = await Spawn("frobnicate");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown command: frobnicate", stderr);
    }
}
