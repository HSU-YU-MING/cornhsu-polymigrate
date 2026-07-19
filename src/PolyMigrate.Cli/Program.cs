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
        return RunVerify(args[1..]);

    case null or "--help" or "-h":
        Console.WriteLine(
            $"""
            polymigrate {version} — the i18n-first static-site migrator

            Usage:
              polymigrate extract <config.yaml> [--root <dir>]
                  Run Phase 2 extraction. Reads <root>/raw and <root>/media, writes
                  <root>/content plus inventories to <root>. Default root: config's directory.
              polymigrate verify <output-dir> [--media <dir>] [--media-prefix /media/]
                  Verify extracted output: frontmatter fields, internal links, media refs.
                  Default media dir: <output-dir>/media (media checks skipped if absent).
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
        Console.WriteLine($"  suggested pairs  : {report.SuggestedPairs} (heuristic, review in content_inventory.csv)");
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

static int RunVerify(string[] args)
{
    string? outDir = null;
    string? mediaDir = null;
    var mediaPrefix = "/media/";
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--media" when i + 1 < args.Length:
                mediaDir = args[++i];
                break;
            case "--media-prefix" when i + 1 < args.Length:
                mediaPrefix = args[++i];
                break;
            default:
                if (outDir is not null)
                {
                    Console.Error.WriteLine($"Unexpected argument: {args[i]}");
                    return 2;
                }
                outDir = args[i];
                break;
        }
    }
    if (outDir is null)
    {
        Console.Error.WriteLine("Usage: polymigrate verify <output-dir> [--media <dir>] [--media-prefix /media/]");
        return 2;
    }
    outDir = Path.GetFullPath(outDir);
    mediaDir = Path.GetFullPath(mediaDir ?? Path.Combine(outDir, "media"));

    var report = new PolyMigrate.Core.Verify.OutputVerifier().Run(outDir, mediaDir, mediaPrefix);

    Console.WriteLine(new string('=', 50));
    Console.WriteLine("Verify done.");
    Console.WriteLine($"  pages checked   : {report.PagesChecked}");
    Console.WriteLine($"  links checked   : {report.LinksChecked}");
    Console.WriteLine(report.MediaChecksSkipped
        ? "  media checks    : skipped (media directory not found)"
        : $"  media refs      : {report.MediaChecked}");
    Console.WriteLine($"  errors          : {report.Errors}");
    Console.WriteLine($"  warnings        : {report.Warnings}");
    Console.WriteLine($"  report          : {Path.Combine(outDir, "verify_report.csv")}");
    Console.WriteLine(new string('=', 50));
    foreach (var issue in report.Issues.Where(i => i.Severity == "error").Take(20))
    {
        Console.WriteLine($"  [error] {issue.Page}: {issue.Kind} {issue.Detail}");
    }
    return report.Errors > 0 ? 2 : report.Warnings > 0 ? 1 : 0;
}

static string Format(SortedDictionary<string, int> counts) =>
    counts.Count == 0 ? "-" : string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
