using System.Text.Json;
using System.Text.RegularExpressions;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// npm 通路的平台清單,五個地方必須是同一組(CLAUDE.md「新增或移除平台時,要一起改的地方」)。
///
/// <para>這張清單的由來就是一次事故:2.1.0 在 release.yml、prepare.mjs、optionalDependencies 三處
/// 加了 arm64,唯獨啟動腳本 bin/polymigrate.js 沒改——平台包被 npm 正確裝好,啟動卻回
/// 「沒有預先建置版本」。CI 的煙霧測試直接執行 linux-x64 執行檔、繞過了啟動腳本,
/// 所以 2.1.0 ~ 2.3.0 的 arm64 使用者全中,沒有任何東西變紅。</para>
///
/// <para>比對單位是 npm 的「平台 架構」(如 <c>linux arm64</c>);.NET RID 依 win→win32、osx→darwin 換算。</para>
/// </summary>
public class NpmPlatformDriftTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine([RepoRoot, .. relative])).Replace("\r", "");

    private static string FromRid(string rid)
    {
        var (os, arch) = (rid[..rid.IndexOf('-')], rid[(rid.IndexOf('-') + 1)..]);
        return $"{os switch { "win" => "win32", "osx" => "darwin", _ => os }} {arch}";
    }

    /// <summary>release.yml「Publish binaries」那一步的 RID 迴圈。</summary>
    private static SortedSet<string> ReleaseWorkflow()
    {
        var m = Regex.Match(Read(".github", "workflows", "release.yml"), @"for rid in (?<rids>[^;\n]+); do");
        Assert.True(m.Success, "release.yml 找不到 `for rid in ...; do` 迴圈");
        return [.. m.Groups["rids"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(FromRid)];
    }

    /// <summary>prepare.mjs 的 TARGETS:每一筆的 rid、os/cpu、套件名三者也要彼此一致。</summary>
    private static SortedSet<string> PrepareScript()
    {
        var entries = Regex.Matches(Read("npm", "prepare.mjs"),
            """\{ rid: "(?<rid>[^"]+)", pkg: "(?<pkg>[^"]+)", os: "(?<os>[^"]+)", cpu: "(?<cpu>[^"]+)", bin: "(?<bin>[^"]+)" \}""");
        Assert.NotEmpty(entries);
        foreach (Match e in entries)
        {
            var platform = $"{e.Groups["os"].Value} {e.Groups["cpu"].Value}";
            Assert.Equal(platform, FromRid(e.Groups["rid"].Value));
            Assert.Equal($"polymigrate-{e.Groups["os"].Value}-{e.Groups["cpu"].Value}", e.Groups["pkg"].Value);
            Assert.Equal(e.Groups["os"].Value == "win32" ? "polymigrate.exe" : "polymigrate", e.Groups["bin"].Value);
        }
        return [.. entries.Select(e => $"{e.Groups["os"].Value} {e.Groups["cpu"].Value}")];
    }

    /// <summary>主套件 package.json 的 optionalDependencies。</summary>
    private static SortedSet<string> PackageJson()
    {
        using var doc = JsonDocument.Parse(Read("npm", "cornhsu-polymigrate", "package.json"));
        var names = doc.RootElement.GetProperty("optionalDependencies").EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(names, n => Assert.Matches("^@cornhsu/polymigrate-[a-z0-9]+-[a-z0-9]+$", n));
        return [.. names.Select(n => n["@cornhsu/polymigrate-".Length..].Replace('-', ' '))];
    }

    /// <summary>啟動腳本 bin/polymigrate.js 的 TARGETS(2.1.0 漏改的那一處)。</summary>
    private static SortedSet<string> LauncherScript()
    {
        var entries = Regex.Matches(Read("npm", "cornhsu-polymigrate", "bin", "polymigrate.js"),
            @"""(?<key>[a-z0-9]+ [a-z0-9]+)"": \{ pkg: ""@cornhsu/polymigrate-(?<pkg>[^""]+)"", bin: ""(?<bin>[^""]+)"" \}");
        Assert.NotEmpty(entries);
        foreach (Match e in entries)
        {
            Assert.Equal(e.Groups["key"].Value.Replace(' ', '-'), e.Groups["pkg"].Value);
            Assert.Equal(e.Groups["key"].Value.StartsWith("win32 ") ? "polymigrate.exe" : "polymigrate", e.Groups["bin"].Value);
        }
        return [.. entries.Select(e => e.Groups["key"].Value)];
    }

    /// <summary>README 的平台敘述「(win32 / linux / darwin, each in x64 and arm64)」= 作業系統 × 架構。</summary>
    private static SortedSet<string> Readme(string file)
    {
        var line = Read(file).Split('\n').Single(l => l.Contains("win32 /", StringComparison.Ordinal));
        var oses = Regex.Matches(line, @"\b(win32|linux|darwin)\b").Select(m => m.Value).Distinct();
        var arches = Regex.Matches(line, @"\b(x64|arm64|ia32|arm)\b").Select(m => m.Value).Distinct().ToList();
        return [.. oses.SelectMany(os => arches.Select(arch => $"{os} {arch}"))];
    }

    [Fact]
    public void AllFivePlaces_ListTheSamePlatforms()
    {
        var launcher = LauncherScript();
        var sources = new Dictionary<string, SortedSet<string>>
        {
            ["release.yml(RID 迴圈)"] = ReleaseWorkflow(),
            ["npm/prepare.mjs(TARGETS)"] = PrepareScript(),
            ["package.json(optionalDependencies)"] = PackageJson(),
            ["bin/polymigrate.js(TARGETS)"] = launcher,
            ["README.md"] = Readme("README.md"),
            ["README.zh-Hant.md"] = Readme("README.zh-Hant.md"),
        };

        // 啟動腳本那份當基準(它是 2.1.0 漏改的那一處);其餘每一處都要與它完全相同
        Assert.Contains("linux arm64", launcher);
        foreach (var (name, platforms) in sources)
        {
            Assert.True(platforms.SetEquals(launcher),
                $"""
                {name} 與啟動腳本的平台清單不一致。
                  只在 {name}:{string.Join(", ", platforms.Except(launcher))}
                  只在 bin/polymigrate.js:{string.Join(", ", launcher.Except(platforms))}
                新增或移除平台時,CLAUDE.md「要一起改的地方」五處都要改。
                """);
        }
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
