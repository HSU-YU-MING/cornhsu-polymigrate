using System.Globalization;
using System.Reflection;
using System.Text;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Diagnostics;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Cli;

/// <summary>
/// polymigrate CLI 的可測進入點。Program.cs 只負責接 Ctrl-C 後呼叫這裡。
/// exit code(§3.8):0 = 乾淨、1 = 有 warning、2 = 有 error、130 = 被中斷(Ctrl-C)。
/// </summary>
public static class Cli
{
    private static readonly string Version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    // 每個指令的語法列只寫在這裡一次:--help 與參數錯誤時印的「Usage:」共用。原本兩邊各抄一份,
    // extract 的 Usage 就少了 --dry-run(文件與程式各自演化一定會走鐘,見 CLAUDE.md「已知教訓」)
    private const string ExtractUsage =
        "polymigrate extract <config.yaml> [--root <dir>] [--raw|--media|--out <dir>] [--dry-run]";
    private const string ThumbsUsage =
        "polymigrate thumbs <config.yaml> [--root <dir>] [--media <dir>] [--out <dir>]";
    private const string ProbeUsage =
        "polymigrate probe-orphans <config.yaml> --section <name> --years <from-to> [--lang <prefix>] [--root <dir>] [--out <file>]";
    private const string FetchUsage =
        "polymigrate fetch-orphans <config.yaml> --section <name> [--slugs <file>] [--root <dir>]";
    private const string VerifyUsage =
        "polymigrate verify <output-dir> [--media <dir>] [--media-prefix /media/] [--allow-unlinked <file>]";
    private const string SlugsUsage =
        "polymigrate slugs <root> --section <name> [--lang <prefix>] [--raw <dir>]";

    /// <summary>
    /// --help 裡的語法列:縮排兩格,太長時在 <c>[…]</c> 之間折行,續行對齊到指令名之後。
    /// 參數錯誤時的 Usage 印的是同一個字串、不折行。
    /// </summary>
    private static string Synopsis(string usage)
    {
        const int width = 92;
        var indent = new string(' ', 2 + usage.IndexOf(' ', "polymigrate ".Length) + 1);
        var segments = new List<string>();
        var start = 0;
        for (var i = usage.IndexOf(" [", StringComparison.Ordinal); i >= 0; i = usage.IndexOf(" [", i + 1, StringComparison.Ordinal))
        {
            segments.Add(usage[start..i]);
            start = i + 1;
        }
        segments.Add(usage[start..]);

        var lines = new List<string>();
        var line = "  " + segments[0];
        foreach (var segment in segments.Skip(1))
        {
            if (line.Length + 1 + segment.Length > width)
            {
                lines.Add(line);
                line = indent + segment;
            }
            else
            {
                line += " " + segment;
            }
        }
        lines.Add(line);
        return string.Join('\n', lines);
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        switch (args.FirstOrDefault())
        {
            case "--version" or "-v":
                Console.WriteLine(Version);
                return 0;

            case "extract":
                return RunExtract(args[1..], ct);

            case "verify":
                return RunVerify(args[1..], ct);

            case "slugs":
                return RunSlugs(args[1..], ct);

            case "thumbs":
                return RunThumbs(args[1..], ct);

            case "probe-orphans":
                return await RunProbeOrphans(args[1..], ct);

            case "fetch-orphans":
                return await RunFetchOrphans(args[1..], ct);

            case null or "--help" or "-h":
                Console.WriteLine(
                    $"""
                    polymigrate {Version} — the i18n-first static-site migrator

                    Usage:
                    {Synopsis(ExtractUsage)}
                          Run Phase 2 extraction. Reads <root>/raw and <root>/media, writes
                          <root>/content plus inventories to <root>. Default root: config's directory.
                          --dry-run: full run and report, but nothing written.
                    {Synopsis(ThumbsUsage)}
                          Generate thumbnails: media/ -> media_thumb/ (same layout). EXIF auto-orient,
                          downscale to media.thumbnails.max_width; animated GIF/WebP keep every frame.
                          Existing thumbnails are skipped.
                    {Synopsis(ProbeUsage)}
                          Probe the live site for orphan pages (removed from indexes but still served):
                          per-day slugs in both YYYYMMDD and MMDDYYYY forms, plus A-D suffix variants.
                          Polite (site.polite.delay_ms) and sends site.auth_workaround cookies.
                          --years: one year (2021) or a range (2021-2023).
                          --lang: defaults to the first url_pattern.lang_map key.
                          --out: defaults to <root>/orphan_slugs.<section>.txt (one list per section).
                          Exits 2 without writing the list when no probe got a usable answer.
                    {Synopsis(FetchUsage)}
                          Fetch probed orphan pages (all langs) into raw/ and their assets into media/.
                          --slugs: defaults to <root>/orphan_slugs.<section>.txt, the list probe-orphans
                          wrote for the same --section (one slug per line).
                    {Synopsis(VerifyUsage)}
                          Verify extracted output: frontmatter fields, internal links, media refs,
                          and unlinked pages — pages no other page links to, which visitors can
                          only reach by typing the URL. Unlinked pages are warnings.
                          Default media dir: <output-dir>/media (media checks skipped if absent).
                          --allow-unlinked: file of pages to exempt, one per line (# comments ok).
                    {Synopsis(SlugsUsage)}
                          List slugs already mirrored under <root>/raw, sorted, one per line on stdout
                          (the summary goes to stderr, so stdout stays pipeable). During a
                          parallel run: diff this against the live site's current list, then
                          feed the difference to fetch-orphans --slugs. Local only, no network.
                      polymigrate --help, -h              Print this help
                      polymigrate --version, -v           Print version

                    For every command that takes <config.yaml>, --root defaults to the config file's directory.
                    """);
                return 0;

            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}. Run 'polymigrate --help'.");
                return 2;
        }
    }

    private static int RunExtract(string[] args, CancellationToken ct)
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
            Console.Error.WriteLine("Usage: " + ExtractUsage);
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

            var report = new ExtractionPipeline(config).Run(paths, dryRun, ct);

            Console.WriteLine(new string('=', 50));
            Console.WriteLine(dryRun ? "Phase 2 dry-run done (nothing written)." : "Phase 2 done.");
            Console.WriteLine($"  markdown files   : {report.PagesWritten}");
            Console.WriteLine($"  translation keys : {report.TranslationKeys}");
            Console.WriteLine($"  types            : {Format(report.TypeCounts)}");
            Console.WriteLine($"  flags            : {Format(report.FlagCounts)}");
            Console.WriteLine($"  single-locale    : {Format(report.OnlyInLocale)}");
            Console.WriteLine($"  suggested pairs  : {report.SuggestedPairs} (heuristic, review in content_inventory.csv)");
            // 「passed validation」不等於「配到了對象」,更不等於「有被用」——
            // pairing.fallback 沒列 hreflang 的話,這兩個數字純粹是觀察,必須講出來
            var hreflangUse = report.HreflangInFallback
                ? "used for pairing"
                : "NOT used — add 'hreflang' to pairing.fallback";
            Console.WriteLine($"  hreflang links   : {report.HreflangDeclared} declared, {report.HreflangUsable} passed validation ({hreflangUse}; why not: hreflang_map.csv)");
            Console.WriteLine($"  media referenced : {report.MediaReferenced}");
            Console.WriteLine($"  missing images   : {report.MissingImages} (recorded in missing_images.csv, non-blocking)");
            Console.WriteLine($"  need-fetch media : {report.NeedFetchMedia} (recorded in need_fetch_media.txt)");
            Console.WriteLine($"  path issues      : {report.PathIssues.Count} ({report.PagesSkippedUnsafe} pages skipped as unsafe, see path_issues.csv)");
            // 沒有這一行的話,「markdown files」與 content/ 裡的實際檔數會從此對不起來,
            // 而且沒有任何地方會提——舊站下架一篇文章重跑一次就會發生
            Console.WriteLine($"  stale in content : {report.StaleOutputs.Count} (present but not written by this run)");
            foreach (var stale in report.StaleOutputs.Take(10))
            {
                Console.WriteLine($"  [stale] {stale}");
            }
            foreach (var (severity, pagePath, issue) in report.PathIssues.Take(10))
            {
                Console.WriteLine($"  [{severity.Wire()}] {pagePath}: {issue}");
            }
            Console.WriteLine(new string('=', 50));
            return report.HasErrors ? 2 : report.HasWarnings ? 1 : 0;
        }
        catch (Exception ex) when (IsHandled(ex))
        {
            return Report(ex);
        }
    }

    private static int RunVerify(string[] args, CancellationToken ct)
    {
        string? outDir = null;
        string? mediaDir = null;
        string? allowUnlinkedFile = null;
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
                case "--allow-unlinked" when i + 1 < args.Length && !args[i + 1].StartsWith('-'):
                    allowUnlinkedFile = args[++i];
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
            Console.Error.WriteLine(
                "Usage: " + VerifyUsage);
            return 2;
        }
        try
        {
            outDir = Path.GetFullPath(outDir);
            mediaDir = Path.GetFullPath(mediaDir ?? Path.Combine(outDir, "media"));
            var allowUnlinked = ReadAllowUnlinked(allowUnlinkedFile);

            var report = new PolyMigrate.Core.Verify.OutputVerifier()
                .Run(outDir, mediaDir, mediaPrefix, allowUnlinked, ct);

            // content/ 不存在時巡檢一開始就停:報告檔不寫、媒體也沒檢查——摘要要照實講,
            // 不能印一個從未寫出的報告路徑,或「media refs : 0」這種看起來像檢查過、零筆的數字
            var noContent = report.Issues.Any(i => i.Kind == "no_content");
            Console.WriteLine(new string('=', 50));
            Console.WriteLine("Verify done.");
            Console.WriteLine($"  pages checked   : {report.PagesChecked}");
            Console.WriteLine($"  links checked   : {report.LinksChecked}");
            Console.WriteLine(
                noContent ? "  media checks    : skipped (content directory not found)"
                : report.MediaChecksSkipped ? "  media checks    : skipped (media directory not found)"
                : $"  media refs      : {report.MediaChecked}");
            Console.WriteLine($"  errors          : {report.Errors}");
            Console.WriteLine($"  warnings        : {report.Warnings}");
            Console.WriteLine(noContent
                ? "  report          : (not written)"
                : $"  report          : {Path.Combine(outDir, "verify_report.csv")}");
            Console.WriteLine(new string('=', 50));
            foreach (var issue in report.Issues.Where(i => i.Severity == Severity.Error).Take(20))
            {
                Console.WriteLine($"  [error] {issue.Page}: {issue.Kind} {issue.Detail}");
            }
            // warning 明細也要印:warning 會讓 exit code 變 1,只給計數等於讓使用者
            // 拿到非零 exit code 卻在畫面上找不到原因,只能自己去翻 verify_report.csv
            foreach (var issue in report.Issues.Where(i => i.Severity == Severity.Warning).Take(20))
            {
                Console.WriteLine($"  [warning] {issue.Page}: {issue.Kind} {issue.Detail}");
            }
            // 收到 unlinked_page 的人多半不是工程師,而且要修的地方根本不在 PolyMigrate
            // (在網站選單)。講一次「該做什麼」與逃生門在哪,不要逐條重複。
            if (report.Issues.Any(i => i.Kind == "unlinked_page"))
            {
                Console.WriteLine("  -> unlinked pages can only be reached by typing the URL. Add a link or a");
                Console.WriteLine("     menu entry, exempt them with --allow-unlinked <file>, or delete them");
                Console.WriteLine("     if they are left over from an earlier run (extract never removes old");
                Console.WriteLine("     output — see 'stale in content' in the extract summary).");
            }
            return report.Errors > 0 ? 2 : report.Warnings > 0 ? 1 : 0;
        }
        catch (Exception ex) when (IsHandled(ex))
        {
            return Report(ex);
        }
    }

    private static int RunSlugs(string[] args, CancellationToken ct)
    {
        const string usage = "Usage: " + SlugsUsage;
        string? root = null;
        string? section = null;
        string? langPrefix = null;
        string? rawDir = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--section" when i + 1 < args.Length && !args[i + 1].StartsWith('-'):
                    section = args[++i];
                    break;
                case "--lang" when i + 1 < args.Length && !args[i + 1].StartsWith('-'):
                    langPrefix = args[++i];
                    break;
                case "--raw" when i + 1 < args.Length && !args[i + 1].StartsWith('-'):
                    rawDir = args[++i];
                    break;
                default:
                    if (root is not null || args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unexpected argument: {args[i]}\n{usage}");
                        return 2;
                    }
                    root = args[i];
                    break;
            }
        }
        if (root is null || section is null)
        {
            Console.Error.WriteLine(usage);
            return 2;
        }
        try
        {
            var raw = Path.GetFullPath(rawDir ?? Path.Combine(Path.GetFullPath(root), "raw"));
            var slugs = PolyMigrate.Core.PolyMigrator.MirrorSlugs(raw, section, langPrefix);
            if (slugs is null)
            {
                // 靜靜回一份空清單會被讀成「沒有新文章」——section 打錯必須是錯誤,不是空結果
                Console.Error.WriteLine($"No mirrored '{section}' directory under {raw}"
                    + (langPrefix is null ? "." : $" for lang '{langPrefix}'."));
                return 2;
            }
            // 列目錄完才檢查取消、而且在寫第一行之前:被中斷時 stdout 一行都不留——半份清單導成檔案後,
            // 會被當成完整的已鏡像清單去求差集
            ct.ThrowIfCancellationRequested();
            // stdout 是資料通道:導成檔案後要餵給 fetch-orphans --slugs,而它以 UTF-8 讀。
            // Windows 上 stdout 跟著 console 碼頁(zh-TW 為 cp950),cmd / Git Bash 導檔時中日韓 slug 會寫成 Big5,
            // 讀回成亂碼網址——所以導到檔案時改以無 BOM 的 UTF-8 寫出(管線與 console 維持原樣,理由見 StandardOutput)
            using var utf8File = StandardOutput.IsRedirectedToFileOnWindows()
                ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), bufferSize: -1, leaveOpen: true)
                : null;
            var output = (TextWriter?)utf8File ?? Console.Out;
            foreach (var slug in slugs)
            {
                output.WriteLine(slug);
            }
            output.Flush();
            // 摘要走 stderr:stdout 只放 slug,才能直接導成檔案再餵給 fetch-orphans --slugs
            Console.Error.WriteLine($"{slugs.Count} slugs mirrored under {section}"
                + (langPrefix is null ? " (all langs)." : $" ({langPrefix})."));
            return 0;
        }
        catch (Exception ex) when (IsHandled(ex))
        {
            return Report(ex);
        }
    }

    private static int RunThumbs(string[] args, CancellationToken ct)
    {
        if (ParseArgs(args, "Usage: " + ThumbsUsage,
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

            var report = new PolyMigrate.Core.Media.ThumbnailGenerator(config).Run(mediaDir, outDir, Console.WriteLine, ct);

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
        catch (Exception ex) when (IsHandled(ex))
        {
            return Report(ex);
        }
    }

    private static async Task<int> RunProbeOrphans(string[] args, CancellationToken ct)
    {
        const string usage = "Usage: " + ProbeUsage;
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
            string outFile;
            if (options.TryGetValue("--out", out var outOption))
            {
                outFile = Path.GetFullPath(outOption);
            }
            else if (DefaultSlugFile(root, section) is { } perSection)
            {
                outFile = perSection;
                NoteLegacySlugFile(root, section, outFile);
            }
            else
            {
                Console.Error.WriteLine($"--section '{section}' can't be part of the default slug file name; pass --out <file>.");
                return 2;
            }
            EnsureWritable(outFile);
            var known = PolyMigrate.Core.Orphans.OrphanProber.KnownSlugs(
                Path.Combine(root, "raw"), langPrefix, section, config.UrlPattern.StripExtensions);   // 與 extract 同一條 slug 規則
            // 與實際請求同一套組法:無語言前綴的站(lang_map 的 key 是 "")不可印出 //news/
            var probedPath = PolyMigrate.Core.Orphans.SitePaths.Join(langPrefix, section);
            Console.WriteLine($"Probing {config.Site.BaseUrl.TrimEnd('/')}/{probedPath}/ for {yearFrom}-{yearTo} "
                + $"({known.Count} known slugs skipped, delay {config.Site.Polite.DelayMs}ms)...");

            using var http = PolyMigrate.Core.Orphans.OrphanProber.CreateHttpClient(config);
            var result = await new PolyMigrate.Core.Orphans.OrphanProber(config, http)
                .ProbeDetailedAsync(langPrefix, section, yearFrom, yearTo, known, Console.WriteLine, ct);
            var found = result.Found;
            // 訊息只用 ASCII:cp437 之類的 console 印不出 × 與 —(會變成問號)
            var unanswered = string.Join(", ", result.Unanswered.Select(kv => $"{kv.Key}: {kv.Value}"));

            // 一個回答都沒拿到(伺服器掛了、被 WAF 擋、base_url 打錯):空清單會被讀成「沒有孤兒頁」,
            // 還會蓋掉先前探好的清單——所以不寫,既有檔原樣保留。而且是 error(exit 2,3.0 起):
            // 2.x 回 0,只看 exit code 的排程分不出「真的沒有」與「根本沒問到」(決策檔 H2 選項 B)
            if (result.Checks > 0 && result.UnansweredCount == result.Checks)
            {
                Console.Error.WriteLine(
                    $"error: none of the {result.Checks} probes got a usable answer ({unanswered})."
                    + " Check site.base_url, site.auth_workaround and site.polite.delay_ms, then rerun.");
                // 2.x 的預設 --out 是各 section 共用的同一個檔,這裡還得警告「留著的可能是別的 section 的清單」;
                // 3.0 起預設檔名帶 section,留著的就是這個 section 先前探到的
                Console.Error.WriteLine(File.Exists(outFile)
                    ? $"warning: {outFile} was left as it was (it holds an earlier probe's list)."
                    : $"warning: {outFile} was not written; an empty list would read as \"no orphan pages\".");
                return 2;
            }

            File.WriteAllText(outFile, string.Join('\n', found));
            Console.WriteLine($"Probe done: {found.Count} orphan slugs -> {outFile}");
            if (result.UnansweredCount > 0)
            {
                Console.Error.WriteLine(
                    $"warning: {result.UnansweredCount} of {result.Checks} probes got no usable answer ({unanswered});"
                    + " the list may be incomplete. Rerun later to fill the gaps.");
            }
            return 0;
        }
        catch (Exception ex) when (IsHandled(ex))
        {
            return Report(ex);
        }
    }

    private static async Task<int> RunFetchOrphans(string[] args, CancellationToken ct)
    {
        const string usage = "Usage: " + FetchUsage;
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
            string slugsFile;
            if (options.TryGetValue("--slugs", out var slugsOption))
            {
                slugsFile = Path.GetFullPath(slugsOption);
                if (!File.Exists(slugsFile))
                {
                    Console.Error.WriteLine($"Slug list not found: {slugsFile} (run probe-orphans first)");
                    return 2;
                }
            }
            else if (DefaultSlugFile(root, section) is { } perSection)
            {
                slugsFile = perSection;
                var legacy = Path.Combine(root, LegacySlugFileName);
                if (!File.Exists(slugsFile))
                {
                    // 2.x 的共用清單不自動讀(它可能是任何一個 section 的),但手上明明有一份清單時,
                    // 只說「找不到」會讓人以為清單不見了:講明它是什麼、怎麼遷移
                    if (File.Exists(legacy))
                    {
                        Console.Error.WriteLine($"Slug list not found: {slugsFile}.");
                        Console.Error.WriteLine(
                            $"{legacy} was written by PolyMigrate 2.x, which shared one slug list across every --section;"
                            + $" 3.0 no longer reads it on its own. If it holds the {section} list, rename it to"
                            + $" orphan_slugs.{section}.txt, or pass --slugs {legacy}.");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Slug list not found: {slugsFile} (run probe-orphans first)");
                    }
                    return 2;
                }
                NoteLegacySlugFile(root, section, slugsFile);
            }
            else
            {
                Console.Error.WriteLine($"--section '{section}' can't be part of the default slug file name; pass --slugs <file>.");
                return 2;
            }
            var slugs = File.ReadAllLines(slugsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            Console.WriteLine($"Fetching {slugs.Count} orphan slugs × {config.UrlPattern.LangMap.Count} langs...");

            using var http = PolyMigrate.Core.Orphans.OrphanProber.CreateHttpClient(config, allowRedirects: true);
            var report = await new PolyMigrate.Core.Orphans.OrphanFetcher(config, http)
                .FetchAsync(slugs, section, Path.Combine(root, "raw"), Path.Combine(root, "media"), Console.WriteLine, ct);

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
        catch (Exception ex) when (IsHandled(ex))
        {
            return Report(ex);
        }
    }

    /// <summary>2.x 的預設清單檔(各 section 共用一份)。3.0 起不讀、不寫、不刪,只用來提示怎麼遷移。</summary>
    private const string LegacySlugFileName = "orphan_slugs.txt";

    /// <summary>
    /// probe 的 <c>--out</c> 與 fetch 的 <c>--slugs</c> 的預設值:<c>&lt;root&gt;/orphan_slugs.&lt;section&gt;.txt</c>
    /// (3.0 起每個 section 一份,決策檔 H2 選項 E)。2.x 各 section 共用 <c>orphan_slugs.txt</c>:探第二個 section
    /// 就蓋掉第一個的清單,一個回答都沒拿到時留下來的也可能是別的 section 的。
    /// section 不是單一名稱(含 <c>/</c> <c>\</c> <c>:</c>、或是 <c>.</c> <c>..</c>)時拼不出檔名 → null,由呼叫端請使用者明確指定。
    /// </summary>
    private static string? DefaultSlugFile(string root, string section) =>
        section is "" or "." or ".." || section.AsSpan().IndexOfAny('/', '\\', ':') >= 0
            ? null
            : Path.GetFullPath(Path.Combine(root, $"orphan_slugs.{section}.txt"));

    /// <summary>用到預設檔名、而 2.x 的共用清單還在時,講一次它不再被讀寫、以及怎麼遷移(不改 exit code)。</summary>
    private static void NoteLegacySlugFile(string root, string section, string perSection)
    {
        var legacy = Path.Combine(root, LegacySlugFileName);
        if (File.Exists(legacy))
        {
            Console.Error.WriteLine(
                $"note: {legacy} was written by PolyMigrate 2.x, which shared one slug list across every --section."
                + $" Since 3.0 the default list is per section ({perSection}) and the old file is no longer read or written."
                + $" If it holds the {section} list, rename it to orphan_slugs.{section}.txt; otherwise delete it.");
        }
    }

    /// <summary>
    /// 跑長工作之前先確認輸出檔寫得出去(寫不出擲 IO 例外 → exit 2)。probe 一年就是 730 個以上的請求,
    /// 預設延遲下約 36 分鐘——原本要整批探完、到最後寫檔時才知道目錄不存在或 --out 指到目錄。
    /// <para>不留痕跡:既有檔以 Append 開啟、不寫任何位元組(內容與修改時間都不變);原本不存在的檔
    /// 檢查完就刪掉——否則探測被中斷或一個回答都沒拿到時,會留下一個空檔,被讀成「沒有孤兒頁」。</para>
    /// </summary>
    private static void EnsureWritable(string file)
    {
        if (Directory.Exists(file))
        {
            throw new IOException($"Output path is a directory, not a file: {file}");
        }
        var existed = File.Exists(file);
        using (new FileStream(file, FileMode.Append, FileAccess.Write))
        {
        }
        if (!existed)
        {
            File.Delete(file);
        }
    }

    /// <summary>豁免清單:一行一頁(content 相對路徑或路由),空行與 # 開頭略過。</summary>
    private static IReadOnlyCollection<string>? ReadAllowUnlinked(string? path)
    {
        if (path is null)
        {
            return null;
        }
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"Allow-unlinked list not found: {full}", full);
        }
        return [.. File.ReadAllLines(full)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))];
    }

    private static bool IsIoError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or FormatException or System.Security.SecurityException;

    /// <summary>
    /// 指令主體共同攔截的例外。每個指令的 catch 一律是這一組——不是各自挑幾種,
    /// 否則新增指令時漏抄一種(例如忘了 OperationCanceledException → 130)不會被任何測試發現。
    /// <para>AngleSharp 的 <c>DomException</c> 直接繼承 Exception,不屬於上面任何一類。config 的 selector
    /// 已在用到之前驗過(壞了是 ConfigException),這裡是最後一道網:漏網的也是 exit 2,
    /// 不會以未處理例外結束(Windows 上是 0xE0434352,不在 0/1/2/130 契約內)。
    /// <b>這一項刻意沒有測試</b>:目前沒有任何路徑會讓 DomException 走到這裡(selector 一律經 CssSelectors
    /// 轉成 ConfigException,其餘 DOM 呼叫的引數都是寫死的合法值),所以拿掉它測試照樣全綠。它防的是日後新增的
    /// DOM 呼叫漏接——那時應該補的是那一處的轉換與測試,而不是依賴這一行。</para>
    /// </summary>
    private static bool IsHandled(Exception ex) =>
        ex is OperationCanceledException or ConfigException or AngleSharp.Dom.DomException || IsIoError(ex);

    /// <summary>
    /// 共同錯誤 → 訊息 + exit code(§3.8)。exit code 契約只在這裡定義一次;
    /// 驗收測試見 CliTests 末段「exit code 契約」那一組。
    /// </summary>
    private static int Report(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                Console.Error.WriteLine("cancelled.");
                return 130;
            case ConfigException:
                Console.Error.WriteLine(ex.Message);
                return 2;
            default:
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
        }
    }

    private static string Format(SortedDictionary<string, int> counts) =>
        counts.Count == 0 ? "-" : string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));

    private static (string ConfigPath, Dictionary<string, string> Options)? ParseArgs(
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

    private static string ResolveRoot(string configPath, Dictionary<string, string> options) =>
        Path.GetFullPath(options.GetValueOrDefault("--root", Path.GetDirectoryName(Path.GetFullPath(configPath))!));
}
