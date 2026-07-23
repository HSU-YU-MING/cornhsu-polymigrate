using System.Globalization;
using System.Reflection;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Diagnostics;
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

    case "thumbs":
        return RunThumbs(args[1..]);

    case "probe-orphans":
        return await RunProbeOrphans(args[1..]);

    case "fetch-orphans":
        return await RunFetchOrphans(args[1..]);

    case null or "--help" or "-h":
        Console.WriteLine(
            $"""
            polymigrate {version} — the i18n-first static-site migrator

            Usage:
              polymigrate extract <config.yaml> [--root <dir>] [--raw|--media|--out <dir>] [--dry-run]
                  Run Phase 2 extraction. Reads <root>/raw and <root>/media, writes
                  <root>/content plus inventories to <root>. Default root: config's directory.
                  --dry-run: full run and report, but nothing written.
              polymigrate thumbs <config.yaml> [--root <dir>] [--media <dir>] [--out <dir>]
                  Generate thumbnails: media/ -> media_thumb/ (same layout). EXIF auto-orient,
                  downscale to media.thumbnails.max_width. Existing thumbnails are skipped.
              polymigrate probe-orphans <config.yaml> --section <name> --years <from-to>
                                        [--lang <prefix>] [--root <dir>] [--out <file>]
                  Probe the live site for orphan pages (removed from indexes but still served):
                  per-day slugs in both YYYYMMDD and MMDDYYYY forms, plus A-D suffix variants.
                  Polite (site.polite.delay_ms) and sends site.auth_workaround cookies.
              polymigrate fetch-orphans <config.yaml> --section <name> [--slugs <file>] [--root <dir>]
                  Fetch probed orphan pages (all langs) into raw/ and their assets into media/.
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
    var dryRun = false;
    var options = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--root" or "--raw" or "--media" or "--out")
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                Console.Error.WriteLine($"Option {args[i]} requires a value.");
                return 2;
            }
            options[args[i]] = args[++i];
        }
        else if (args[i] == "--dry-run")
        {
            dryRun = true;
        }
        else if (configPath is null && !args[i].StartsWith('-'))
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
        if (!dryRun)
        {
            Directory.CreateDirectory(paths.OutDir);
        }
        if (!Directory.Exists(paths.RawDir))
        {
            Console.Error.WriteLine($"Raw mirror directory not found: {paths.RawDir}");
            return 2;
        }

        var report = new ExtractionPipeline(config).Run(paths, dryRun);

        Console.WriteLine(new string('=', 50));
        Console.WriteLine(dryRun ? "Phase 2 dry-run done (nothing written)." : "Phase 2 done.");
        Console.WriteLine($"  markdown files   : {report.PagesWritten}");
        Console.WriteLine($"  translation keys : {report.TranslationKeys}");
        Console.WriteLine($"  types            : {Format(report.TypeCounts)}");
        Console.WriteLine($"  flags            : {Format(report.FlagCounts)}");
        Console.WriteLine($"  single-locale    : {Format(report.OnlyInLocale)}");
        Console.WriteLine($"  suggested pairs  : {report.SuggestedPairs} (heuristic, review in content_inventory.csv)");
        Console.WriteLine($"  media referenced : {report.MediaReferenced}");
        Console.WriteLine($"  missing images   : {report.MissingImages} (recorded in missing_images.csv, non-blocking)");
        Console.WriteLine($"  need-fetch media : {report.NeedFetchMedia} (recorded in need_fetch_media.txt)");
        Console.WriteLine($"  path issues      : {report.PathIssues.Count} ({report.PagesSkippedUnsafe} pages skipped as unsafe, see path_issues.csv)");
        foreach (var (severity, pagePath, issue) in report.PathIssues.Take(10))
        {
            Console.WriteLine($"  [{severity.Wire()}] {pagePath}: {issue}");
        }
        Console.WriteLine(new string('=', 50));
        return report.HasErrors ? 2 : report.HasWarnings ? 1 : 0;
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
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
            case "--media" when i + 1 < args.Length && !args[i + 1].StartsWith('-'):
                mediaDir = args[++i];
                break;
            case "--media-prefix" when i + 1 < args.Length && !args[i + 1].StartsWith('-'):
                mediaPrefix = args[++i];
                break;
            default:
                if (outDir is not null || args[i].StartsWith('-'))
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
    try
    {
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
        foreach (var issue in report.Issues.Where(i => i.Severity == Severity.Error).Take(20))
        {
            Console.WriteLine($"  [error] {issue.Page}: {issue.Kind} {issue.Detail}");
        }
        return report.Errors > 0 ? 2 : report.Warnings > 0 ? 1 : 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
}

static string Format(SortedDictionary<string, int> counts) =>
    counts.Count == 0 ? "-" : string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));

static (string ConfigPath, Dictionary<string, string> Options)? ParseArgs(
    string[] args, string usage, params string[] knownOptions)
{
    string? configPath = null;
    var options = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (knownOptions.Contains(args[i]))
        {
            if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
            {
                Console.Error.WriteLine($"Option {args[i]} requires a value.\n{usage}");
                return null;
            }
            options[args[i]] = args[++i];
        }
        else if (configPath is null && !args[i].StartsWith('-'))
        {
            configPath = args[i];
        }
        else
        {
            Console.Error.WriteLine($"Unexpected argument: {args[i]}\n{usage}");
            return null;
        }
    }
    if (configPath is null)
    {
        Console.Error.WriteLine(usage);
        return null;
    }
    return (configPath, options);
}

static string ResolveRoot(string configPath, Dictionary<string, string> options) =>
    Path.GetFullPath(options.GetValueOrDefault("--root", Path.GetDirectoryName(Path.GetFullPath(configPath))!));

static int RunThumbs(string[] args)
{
    if (ParseArgs(args, "Usage: polymigrate thumbs <config.yaml> [--root <dir>] [--media <dir>] [--out <dir>]",
            "--root", "--media", "--out") is not var (configPath, options))
    {
        return 2;
    }
    try
    {
        var config = SiteConfigLoader.LoadFile(configPath);
        var root = ResolveRoot(configPath, options);
        var mediaDir = Path.GetFullPath(options.GetValueOrDefault("--media", Path.Combine(root, "media")));
        var outDir = Path.GetFullPath(options.GetValueOrDefault("--out", Path.Combine(root, "media_thumb")));
        if (!Directory.Exists(mediaDir))
        {
            Console.Error.WriteLine($"Media directory not found: {mediaDir}");
            return 2;
        }

        var report = new PolyMigrate.Core.Media.ThumbnailGenerator(config).Run(mediaDir, outDir, Console.WriteLine);

        Console.WriteLine(new string('=', 50));
        Console.WriteLine("Thumbs done.");
        Console.WriteLine($"  created : {report.Created}");
        Console.WriteLine($"  skipped : {report.Skipped} (already exist)");
        Console.WriteLine($"  failed  : {report.Failed}");
        foreach (var (path, error) in report.Failures.Take(10))
        {
            Console.WriteLine($"  [fail] {path} :: {error}");
        }
        Console.WriteLine(new string('=', 50));
        return report.Failed > 0 ? 1 : 0;
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
}

static async Task<int> RunProbeOrphans(string[] args)
{
    const string usage = "Usage: polymigrate probe-orphans <config.yaml> --section <name> --years <from-to> [--lang <prefix>] [--root <dir>] [--out <file>]";
    if (ParseArgs(args, usage, "--section", "--years", "--lang", "--root", "--out") is not var (configPath, options))
    {
        return 2;
    }
    if (!options.TryGetValue("--section", out var section) || !options.TryGetValue("--years", out var years))
    {
        Console.Error.WriteLine(usage);
        return 2;
    }
    var parts = years.Split('-');
    if (parts.Length is not (1 or 2)
        || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var yearFrom)
        || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var yearTo)
        || yearFrom > yearTo)
    {
        Console.Error.WriteLine($"Invalid --years '{years}' (expected e.g. 2021-2023).");
        return 2;
    }

    try
    {
        var config = SiteConfigLoader.LoadFile(configPath);
        var langPrefix = options.GetValueOrDefault("--lang", config.UrlPattern.LangMap.Keys.First());
        var root = ResolveRoot(configPath, options);
        var outFile = Path.GetFullPath(options.GetValueOrDefault("--out", Path.Combine(root, "orphan_slugs.txt")));
        var known = PolyMigrate.Core.Orphans.OrphanProber.KnownSlugs(Path.Combine(root, "raw"), langPrefix, section);
        Console.WriteLine($"Probing {config.Site.BaseUrl}/{langPrefix}/{section}/ for {yearFrom}-{yearTo} "
            + $"({known.Count} known slugs skipped, delay {config.Site.Polite.DelayMs}ms)...");

        using var http = PolyMigrate.Core.Orphans.OrphanProber.CreateHttpClient(config);
        var found = await new PolyMigrate.Core.Orphans.OrphanProber(config, http)
            .ProbeAsync(langPrefix, section, yearFrom, yearTo, known, Console.WriteLine);

        File.WriteAllText(outFile, string.Join('\n', found));
        Console.WriteLine($"Probe done: {found.Count} orphan slugs -> {outFile}");
        return 0;
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
}

static async Task<int> RunFetchOrphans(string[] args)
{
    const string usage = "Usage: polymigrate fetch-orphans <config.yaml> --section <name> [--slugs <file>] [--root <dir>]";
    if (ParseArgs(args, usage, "--section", "--slugs", "--root") is not var (configPath, options))
    {
        return 2;
    }
    if (!options.TryGetValue("--section", out var section))
    {
        Console.Error.WriteLine(usage);
        return 2;
    }

    try
    {
        var config = SiteConfigLoader.LoadFile(configPath);
        var root = ResolveRoot(configPath, options);
        var slugsFile = Path.GetFullPath(options.GetValueOrDefault("--slugs", Path.Combine(root, "orphan_slugs.txt")));
        if (!File.Exists(slugsFile))
        {
            Console.Error.WriteLine($"Slug list not found: {slugsFile} (run probe-orphans first)");
            return 2;
        }
        var slugs = File.ReadAllLines(slugsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Console.WriteLine($"Fetching {slugs.Count} orphan slugs × {config.UrlPattern.LangMap.Count} langs...");

        using var http = PolyMigrate.Core.Orphans.OrphanProber.CreateHttpClient(config, allowRedirects: true);
        var report = await new PolyMigrate.Core.Orphans.OrphanFetcher(config, http)
            .FetchAsync(slugs, section, Path.Combine(root, "raw"), Path.Combine(root, "media"), Console.WriteLine);

        Console.WriteLine(new string('=', 50));
        Console.WriteLine("Fetch done.");
        Console.WriteLine($"  pages fetched : {report.PagesFetched}");
        Console.WriteLine($"  pages skipped : {report.PagesSkipped} (already mirrored)");
        Console.WriteLine($"  assets fetched: {report.AssetsFetched}");
        foreach (var error in report.Errors.Take(10))
        {
            Console.WriteLine($"  {error}");
        }
        Console.WriteLine(new string('=', 50));
        return report.Errors.Count > 0 ? 1 : 0;
    }
    catch (ConfigException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }
}
