using System.Reflection;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

// 極簡進入點;之後如需複雜選項解析再引入 System.CommandLine。
// exit code(§3.8):0 = 乾淨、1 = 有 warning、2 = 有 error。

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

switch (args.FirstOrDefault())
{
    case "--version" or "-v":
        Console.WriteLine(version);
        return 0;

    case "extract":
        return RunExtract(args[1..]);

    case "verify":
        Console.Error.WriteLine("'verify' is not implemented yet.");
        return 2;

    case null or "--help" or "-h":
        Console.WriteLine(
            $"""
            polymigrate {version} — the i18n-first static-site migrator

            Usage:
              polymigrate extract <config.yaml> [--root <dir>]
                  Run Phase 2 extraction. Reads <root>/raw and <root>/media, writes
                  <root>/content plus inventories to <root>. Default root: config's directory.
              polymigrate verify <output-dir>     Verify migrated output (not implemented)
              polymigrate --version               Print version
            """);
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command: {args[0]}. Run 'polymigrate --help'.");
        return 2;
}

static int RunExtract(string[] args)
{
    string? configPath = null;
    var options = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--root" or "--raw" or "--media" or "--out" && i + 1 < args.Length)
        {
            options[args[i]] = args[++i];
        }
        else if (configPath is null)
        {
            configPath = args[i];
        }
        else
        {
            Console.Error.WriteLine($"Unexpected argument: {args[i]}");
            return 2;
        }
    }
    if (configPath is null)
    {
        Console.Error.WriteLine("Usage: polymigrate extract <config.yaml> [--root <dir>] [--raw <dir>] [--media <dir>] [--out <dir>]");
        return 2;
    }

    try
    {
        var config = SiteConfigLoader.LoadFile(configPath);
        var root = Path.GetFullPath(
            options.GetValueOrDefault("--root", Path.GetDirectoryName(Path.GetFullPath(configPath))!));
        var defaults = ExtractionPaths.ForRoot(root);
        var paths = new ExtractionPaths(
            Path.GetFullPath(options.GetValueOrDefault("--raw", defaults.RawDir)),
            Path.GetFullPath(options.GetValueOrDefault("--media", defaults.MediaDir)),
            Path.GetFullPath(options.GetValueOrDefault("--out", defaults.OutDir)));
        Directory.CreateDirectory(paths.OutDir);
        if (!Directory.Exists(paths.RawDir))
        {
            Console.Error.WriteLine($"Raw mirror directory not found: {paths.RawDir}");
            return 2;
        }

        var report = new ExtractionPipeline(config).Run(paths);

        Console.WriteLine(new string('=', 50));
        Console.WriteLine("Phase 2 done.");
        Console.WriteLine($"  markdown files   : {report.PagesWritten}");
        Console.WriteLine($"  translation keys : {report.TranslationKeys}");
        Console.WriteLine($"  types            : {Format(report.TypeCounts)}");
        Console.WriteLine($"  flags            : {Format(report.FlagCounts)}");
        Console.WriteLine($"  single-locale    : {Format(report.OnlyInLocale)}");
        Console.WriteLine($"  media referenced : {report.MediaReferenced}");
        Console.WriteLine($"  missing images   : {report.MissingImages} (recorded in missing_images.csv, non-blocking)");
        Console.WriteLine($"  need-fetch media : {report.NeedFetchMedia} (recorded in need_fetch_media.txt)");
        Console.WriteLine(new string('=', 50));
        return report.HasWarnings ? 1 : 0;
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

static string Format(SortedDictionary<string, int> counts) =>
    counts.Count == 0 ? "-" : string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
