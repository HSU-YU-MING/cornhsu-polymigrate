namespace PolyMigrate.Cli.Tests;

/// <summary>一次 CLI 執行的結果;Out / Err 已把 \r 去掉(Console.WriteLine 在 Windows 是 CRLF)。</summary>
public sealed record CliResult(int Exit, string Out, string Err);

/// <summary>
/// 各子指令測試共用的底座:Console 重導向、拋棄式目錄、寫檔小工具。
/// Console 是行程級全域——本組件已關掉平行測試(見 CliTests 的 assembly 屬性)。
/// </summary>
public abstract class CliTestBase : IDisposable
{
    protected static readonly string RepoRoot = FindRepoRoot();

    /// <summary>每個測試自己的拋棄式根目錄。</summary>
    protected readonly string Root = Directory.CreateTempSubdirectory("polymigrate-cli").FullName;

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }

    protected static Task<CliResult> Run(params string[] args) => RunWith(CancellationToken.None, args);

    protected static async Task<CliResult> RunWith(CancellationToken ct, params string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        var so = new StringWriter();
        var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var exit = await Cli.RunAsync(args, ct);
            return new CliResult(exit, so.ToString().Replace("\r", ""), se.ToString().Replace("\r", ""));
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    /// <summary>在 <see cref="Root"/> 底下寫一個檔('/' 分隔的相對路徑),回傳絕對路徑。</summary>
    protected string Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    protected string PathOf(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>把 tests/fixtures/site(config + raw + media)複製到 <paramref name="relative"/>,回傳 config 路徑。</summary>
    protected string CopyFixtureSite(string relative = "site")
    {
        var source = Path.Combine(RepoRoot, "tests", "fixtures", "site");
        var target = PathOf(relative);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        return Path.Combine(target, "config.yaml");
    }

    /// <summary>最小合法 config:單一 ch 語言、正文 = &lt;main&gt;、不碰網路(delay 0)。</summary>
    protected static string Config(string baseUrl = "https://example.invalid", string extraExtract = "",
        string langMap = "{ ch: zh-Hant }", string extraTop = "") =>
        $"""
        config_version: 1
        site:
          base_url: {baseUrl}
          polite:
            delay_ms: 0
        url_pattern:
          lang_map: {langMap}
          default_lang: zh-Hant
        extract:
          content: "main"
        {extraExtract}
        {extraTop}
        """;

    protected static string Page(string body) =>
        $"<html><head><title>t</title></head><body><main>{body}</main></body></html>";

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
