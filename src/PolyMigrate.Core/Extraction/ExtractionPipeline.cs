using System.Text;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Inventory;
using PolyMigrate.Core.Markdown;
using PolyMigrate.Core.Pairing;

namespace PolyMigrate.Core.Extraction;

/// <summary>Phase 2 輸入/輸出位置。預設佈局:root/raw、root/media,輸出至 root。</summary>
public sealed record ExtractionPaths(string RawDir, string MediaDir, string OutDir)
{
    public static ExtractionPaths ForRoot(string root) => new(
        Path.Combine(root, "raw"),
        Path.Combine(root, "media"),
        root);
}

/// <summary>執行摘要(§3.8)。missing/need-fetch 屬 warning:記錄、不阻斷。</summary>
public sealed class ExtractionReport
{
    public int PagesWritten { get; init; }

    public int TranslationKeys { get; init; }

    public required SortedDictionary<string, int> TypeCounts { get; init; }

    public required SortedDictionary<string, int> FlagCounts { get; init; }

    /// <summary>locale → 只有該語言版本的 translation_key 數(規格 only_ch/only_en 的一般化)。</summary>
    public required SortedDictionary<string, int> OnlyInLocale { get; init; }

    public int MediaReferenced { get; init; }

    public int MissingImages { get; init; }

    public int NeedFetchMedia { get; init; }

    /// <summary>啟發式配對建議的組數(§1.4),待人工覆核。</summary>
    public int SuggestedPairs { get; init; }

    /// <summary>路徑安全問題(§3.4):error = 拒寫並跳過該頁;warning = 照寫但記錄(如超長路徑)。</summary>
    public required List<(string Severity, string Page, string Issue)> PathIssues { get; init; }

    public int PagesSkippedUnsafe => PathIssues.Count(i => i.Severity == "error");

    public bool HasErrors => PagesSkippedUnsafe > 0;

    public bool HasWarnings => MissingImages > 0 || NeedFetchMedia > 0
        || PathIssues.Any(i => i.Severity == "warning");
}

/// <summary>
/// Phase 2 管線(規格 main):巡鏡像檔 → 逐頁抽取寫 Markdown → 聚合寫四份清單與待補媒體清單。
/// 純本地、可重跑,不碰伺服器。
/// </summary>
public sealed class ExtractionPipeline(SiteConfig config)
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <param name="paths">輸入/輸出位置。</param>
    /// <param name="dryRun">true = 完整跑抽取與統計但不寫任何檔案(§3.8)。</param>
    public ExtractionReport Run(ExtractionPaths paths, bool dryRun = false)
    {
        var parser = new RawPageParser(config.Site, config.UrlPattern);
        var extractor = new PageExtractor(config);
        var links = new LinkRewriter(config.Site, config.UrlPattern);
        var encoding = TextEncodings.Resolve(config.Site.Encoding);
        var locales = config.UrlPattern.LangMap.Values.Distinct().ToList();

        var files = Directory.EnumerateFiles(paths.RawDir, "*.html", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetRelativePath(paths.RawDir, f).Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();

        var aggregator = new InventoryAggregator(links);
        var pathIssues = new List<(string Severity, string Page, string Issue)>();
        var seenPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var pagesWritten = 0;

        foreach (var file in files)
        {
            var page = parser.Parse(paths.RawDir, file);
            var extracted = extractor.Extract(page, File.ReadAllText(file, encoding), paths.MediaDir);

            // §3.4:不安全路徑在「任何平台」都一致地拒寫並記錄——兩平台產出必須相同
            var relPath = MarkdownRelativePath(page);
            var unsafeIssue = PathSafety.Check(relPath) ?? PathSafety.RegisterOrCollide(seenPaths, relPath);
            if (unsafeIssue is not null)
            {
                pathIssues.Add(("error", relPath, unsafeIssue));
            }
            else
            {
                if (PathSafety.CheckLength(Path.GetFullPath(Path.Combine(paths.OutDir, relPath))) is { } lengthIssue)
                {
                    pathIssues.Add(("warning", relPath, lengthIssue));
                }
                if (!dryRun)
                {
                    WriteMarkdown(paths.OutDir, relPath, extracted);
                }
                pagesWritten++;
            }

            // translation_key 聚合各語言版本 + 媒體引用/缺圖/待補/redirect 的摺疊(純記憶體,見 InventoryAggregator)
            aggregator.Add(page, extracted);
        }

        var suggestions = SuggestPairs(aggregator.Inventory, locales);
        if (!dryRun)
        {
            WriteContentInventory(paths.OutDir, aggregator.Inventory, locales, suggestions);
            WriteMediaManifest(paths.OutDir, paths.MediaDir, aggregator.MediaRefs);
            WriteRedirectMap(paths.OutDir, aggregator.Redirects);
            WriteRedirectExports(paths.OutDir, aggregator.Redirects);
            WriteMissingImages(paths.OutDir, aggregator.Missing);
            WritePathIssues(paths.OutDir, pathIssues);
            File.WriteAllText(Path.Combine(paths.OutDir, "need_fetch_media.txt"),
                string.Join('\n', aggregator.NeedFetch), Utf8NoBom);
        }

        return BuildReport(aggregator.Inventory, locales, pagesWritten, aggregator.MediaRefs.Count,
            aggregator.Missing.Count, aggregator.NeedFetch.Count, suggestions.Count / 2, pathIssues);
    }

    private static string MarkdownRelativePath(RawPage page)
    {
        string[] segments = ["content", page.LangPrefix, page.Section, page.Slug + ".md"];
        return string.Join('/', segments.Where(s => s.Length > 0));
    }

    private static void WriteMarkdown(string outDir, string relativePath, ExtractedPage extracted)
    {
        var page = extracted.Page;
        var target = Path.Combine(outDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // frontmatter:欄位序 = 規格 fm_data 序(lang 改輸出 BCP-47 locale,§3.3)
        var frontmatter = new Dictionary<string, object?>
        {
            ["source_url"] = page.SourceUrl,
            ["lang"] = page.Locale,
            ["section"] = page.Section,
            ["slug"] = page.Slug,
            ["translation_key"] = page.TranslationKey,
            ["title"] = extracted.Title,
            ["page_type"] = extracted.PageType,
            ["flags"] = extracted.Flags,
            ["text_length"] = extracted.TextLength,
            ["image_count"] = extracted.ImageCount,
            ["images"] = extracted.Images
                .Select(i => new Dictionary<string, string> { ["local"] = i.Web, ["alt"] = i.Alt })
                .ToList(),
            ["videos"] = extracted.Videos,
            ["documents"] = extracted.Documents,
        };
        File.WriteAllText(target,
            FrontmatterSerializer.ToBlock(frontmatter) + extracted.BodyMarkdown + "\n",
            Utf8NoBom);
    }

    private static string LocaleColumn(string locale) => locale.Replace('-', '_').ToLowerInvariant();

    /// <summary>對「只有單語」的 key 產生啟發式配對建議(§1.4);回傳 key → 建議,兩端各一筆。</summary>
    private Dictionary<string, PairSuggestion> SuggestPairs(
        SortedDictionary<string, InventoryRecord> inventory, List<string> locales)
    {
        if (locales.Count < 2)
        {
            return [];
        }
        var unpaired = inventory
            .Where(kv => !kv.Key.StartsWith('/') && kv.Value.TextByLocale.Count == 1)
            .Select(kv => new UnpairedGroup
            {
                TranslationKey = kv.Key,
                Section = kv.Value.Section,
                Locale = kv.Value.TextByLocale.Keys.Single(),
                Slug = kv.Value.Slug,
                Title = kv.Value.Title,
                Media = kv.Value.Media,
            });
        var byKey = new Dictionary<string, PairSuggestion>();
        foreach (var s in new PairingSuggester(config).Suggest(unpaired))
        {
            byKey[s.KeyA] = s;
            byKey[s.KeyB] = s;
        }
        return byKey;
    }

    private string PairStatus(string key, InventoryRecord r, List<string> locales,
        Dictionary<string, PairSuggestion> suggestions)
    {
        if (locales.Count < 2)
        {
            return "";                       // 單語站:配對不適用
        }
        if (key.StartsWith('/'))
        {
            return "site_level";             // 站級頁(語言選擇頁等)不參與配對
        }
        if (locales.All(r.TextByLocale.ContainsKey))
        {
            return "paired";
        }
        return suggestions.ContainsKey(key) ? "heuristic_suggested" : "missing";
    }

    private void WriteContentInventory(
        string outDir, SortedDictionary<string, InventoryRecord> inventory, List<string> locales,
        Dictionary<string, PairSuggestion> suggestions)
    {
        var rows = new List<IReadOnlyList<string>>();
        string[] header =
        [
            "translation_key", "section", "slug",
            .. locales.Select(l => $"has_{LocaleColumn(l)}"),
            "suggested_type", "final_type", "flags",
            "pair_status", "suggested_pair", "pair_evidence",
            .. locales.Select(l => $"text_len_{LocaleColumn(l)}"),
            "image_count", "notes",
        ];
        rows.Add(header);
        foreach (var (key, r) in inventory)
        {
            var suggestion = suggestions.GetValueOrDefault(key);
            string[] row =
            [
                key, r.Section, r.Slug,
                .. locales.Select(l => r.TextByLocale.ContainsKey(l) ? "True" : "False"),
                r.Type, "", string.Join(';', r.Flags),
                PairStatus(key, r, locales, suggestions),
                suggestion is null ? "" : (suggestion.KeyA == key ? suggestion.KeyB : suggestion.KeyA),
                suggestion?.Evidence ?? "",
                .. locales.Select(l => (r.TextByLocale.GetValueOrDefault(l) ?? 0).ToString()),
                r.ImageCount.ToString(), "",
            ];
            rows.Add(row);
        }
        Csv.Write(Path.Combine(outDir, "content_inventory.csv"), rows);
    }

    private static void WriteMediaManifest(
        string outDir, string mediaDir, SortedDictionary<string, MediaEntry> mediaRefs)
    {
        // 雜湊快取:沒動過的檔案免重讀(重跑數 GB 媒體從分鐘級降到秒級)
        var cache = MediaHashCache.Load(Path.Combine(outDir, ".polymigrate", "media_sha1_cache.csv"));
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "local_path", "original_url", "referenced_by", "alt", "sha1", "bytes" },
        };
        foreach (var (relative, entry) in mediaRefs)
        {
            var localAbs = MediaPaths.LocalPath(mediaDir, relative);
            var localPath = Path.GetRelativePath(outDir, localAbs).Replace('\\', '/');
            if (localPath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(localPath))
            {
                // media 不在輸出目錄下(或跨磁碟)→ 一律用中性的 media/ 相對路徑,輸出不可含機器路徑
                localPath = "media/" + relative;
            }
            var (sha1, bytes) = cache.GetOrCompute(relative, localAbs);
            rows.Add(new[] { localPath, entry.OriginalUrl,
                string.Join(" | ", entry.Refs), string.Join(" | ", entry.Alts), sha1, bytes });
        }
        Csv.Write(Path.Combine(outDir, "media_manifest.csv"), rows);
        cache.Save();
    }

    private static void WriteRedirectMap(string outDir, List<Redirect> redirects)
    {
        // new_path 由 LinkRewriter 同一套路由規則自動填(與內文連結改寫一致);人工可在 CSV 覆改
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "old_url", "new_path", "lang", "translation_key" },
        };
        rows.AddRange(redirects.Select(r => new[] { r.OldUrl, r.NewPath, r.Locale, r.TranslationKey }));
        Csv.Write(Path.Combine(outDir, "redirect_map.csv"), rows);
    }

    /// <summary>301 設定檔直接可用的兩種格式:nginx location 區塊與 Netlify _redirects。</summary>
    private static void WriteRedirectExports(string outDir, List<Redirect> redirects)
    {
        var pairs = redirects
            .Select(r => (Old: new Uri(r.OldUrl).AbsolutePath, New: r.NewPath))
            .Where(p => p.Old != p.New)          // 相同路徑不出 301,避免轉址迴圈
            .OrderBy(p => p.Old, StringComparer.Ordinal)
            .ToList();

        File.WriteAllText(Path.Combine(outDir, "redirects.nginx.conf"),
            string.Join('\n', pairs.Select(p => $"location = {p.Old} {{ return 301 {p.New}; }}")) + "\n",
            Utf8NoBom);
        File.WriteAllText(Path.Combine(outDir, "_redirects"),
            string.Join('\n', pairs.Select(p => $"{p.Old} {p.New} 301")) + "\n",
            Utf8NoBom);
    }

    private static void WritePathIssues(string outDir, List<(string Severity, string Page, string Issue)> issues)
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "severity", "page", "issue" } };
        rows.AddRange(issues
            .OrderBy(i => i.Severity, StringComparer.Ordinal)
            .ThenBy(i => i.Page, StringComparer.Ordinal)
            .Select(i => new[] { i.Severity, i.Page, i.Issue }));
        Csv.Write(Path.Combine(outDir, "path_issues.csv"), rows);
    }

    private static void WriteMissingImages(string outDir, List<MissingImage> missing)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "source_page", "missing_image" },
        };
        rows.AddRange(missing.Select(m => new[] { m.SourcePage, m.WebPath }));
        Csv.Write(Path.Combine(outDir, "missing_images.csv"), rows);
    }

    private static ExtractionReport BuildReport(
        SortedDictionary<string, InventoryRecord> inventory, List<string> locales,
        int pagesWritten, int mediaReferenced, int missingCount, int needFetchCount, int suggestedPairs,
        List<(string Severity, string Page, string Issue)> pathIssues)
    {
        var typeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var flagCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var onlyInLocale = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in inventory.Values)
        {
            typeCounts[r.Type] = typeCounts.GetValueOrDefault(r.Type) + 1;
            foreach (var flag in r.Flags)
            {
                flagCounts[flag] = flagCounts.GetValueOrDefault(flag) + 1;
            }
            var present = locales.Where(r.TextByLocale.ContainsKey).ToList();
            if (present.Count == 1 && locales.Count > 1)
            {
                onlyInLocale[present[0]] = onlyInLocale.GetValueOrDefault(present[0]) + 1;
            }
        }
        return new ExtractionReport
        {
            PagesWritten = pagesWritten,
            TranslationKeys = inventory.Count,
            TypeCounts = typeCounts,
            FlagCounts = flagCounts,
            OnlyInLocale = onlyInLocale,
            MediaReferenced = mediaReferenced,
            MissingImages = missingCount,
            NeedFetchMedia = needFetchCount,
            SuggestedPairs = suggestedPairs,
            PathIssues = pathIssues,
        };
    }
}
