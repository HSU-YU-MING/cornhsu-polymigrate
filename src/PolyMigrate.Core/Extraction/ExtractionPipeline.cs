using System.Security.Cryptography;
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

    public bool HasWarnings => MissingImages > 0 || NeedFetchMedia > 0;
}

/// <summary>
/// Phase 2 管線(規格 main):巡鏡像檔 → 逐頁抽取寫 Markdown → 聚合寫四份清單與待補媒體清單。
/// 純本地、可重跑,不碰伺服器。
/// </summary>
public sealed class ExtractionPipeline(SiteConfig config)
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private sealed class InventoryRecord
    {
        public required string Section { get; init; }

        public required string Slug { get; init; }

        public required string Type { get; init; }

        public required string Title { get; init; }   // 首見版本的標題(單語 key 即該語標題)

        public Dictionary<string, int?> TextByLocale { get; } = [];   // null = 該語言缺

        public SortedSet<string> Flags { get; } = [];

        public SortedSet<string> Media { get; } = new(StringComparer.Ordinal);

        public int ImageCount { get; set; }
    }

    public ExtractionReport Run(ExtractionPaths paths)
    {
        var parser = new RawPageParser(config);
        var extractor = new PageExtractor(config);
        var encoding = TextEncodings.Resolve(config.Site.Encoding);
        var locales = config.UrlPattern.LangMap.Values.Distinct().ToList();

        var files = Directory.EnumerateFiles(paths.RawDir, "*.html", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetRelativePath(paths.RawDir, f).Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();

        var inventory = new SortedDictionary<string, InventoryRecord>(StringComparer.Ordinal);
        var mediaRefs = new SortedDictionary<string, (string Orig, SortedSet<string> Refs, SortedSet<string> Alts)>(StringComparer.Ordinal);
        var redirects = new List<(string OldUrl, string Locale, string TranslationKey)>();
        var missing = new List<MissingImage>();
        var needFetch = new SortedSet<string>(StringComparer.Ordinal);
        var pagesWritten = 0;

        foreach (var file in files)
        {
            var page = parser.Parse(paths.RawDir, file);
            var extracted = extractor.Extract(page, File.ReadAllText(file, encoding), paths.MediaDir);

            WriteMarkdown(paths.OutDir, extracted);
            pagesWritten++;

            // inventory 以 translation_key 聚合各語言版本
            if (!inventory.TryGetValue(page.TranslationKey, out var record))
            {
                record = new InventoryRecord
                {
                    Section = page.Section,
                    Slug = page.Slug,
                    Type = extracted.PageType,
                    Title = extracted.Title,
                };
                inventory[page.TranslationKey] = record;
            }
            record.TextByLocale[page.Locale] = extracted.TextLength;
            record.ImageCount = Math.Max(record.ImageCount, extracted.ImageCount);
            record.Flags.UnionWith(extracted.Flags);
            record.Media.UnionWith(extracted.MediaUses.Select(u => u.MediaRelative));

            foreach (var use in extracted.MediaUses)
            {
                if (!mediaRefs.TryGetValue(use.MediaRelative, out var entry))
                {
                    entry = (use.OriginalUrl, new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                    mediaRefs[use.MediaRelative] = entry;
                }
                entry.Refs.Add(use.SourceUrl);
                if (use.Alt.Length > 0)
                {
                    entry.Alts.Add(use.Alt);
                }
            }
            missing.AddRange(extracted.MissingImages);
            needFetch.UnionWith(extracted.NeedFetch);
            redirects.Add((page.SourceUrl, page.Locale, page.TranslationKey));
        }

        var suggestions = SuggestPairs(inventory, locales);
        WriteContentInventory(paths.OutDir, inventory, locales, suggestions);
        WriteMediaManifest(paths.OutDir, paths.MediaDir, mediaRefs);
        WriteRedirectMap(paths.OutDir, redirects);
        WriteMissingImages(paths.OutDir, missing);
        File.WriteAllText(Path.Combine(paths.OutDir, "need_fetch_media.txt"),
            string.Join('\n', needFetch), Utf8NoBom);

        return BuildReport(inventory, locales, pagesWritten, mediaRefs.Count, missing.Count, needFetch.Count,
            suggestions.Count / 2);
    }

    private static void WriteMarkdown(string outDir, ExtractedPage extracted)
    {
        var page = extracted.Page;
        string[] segments = [outDir, "content", page.LangPrefix, page.Section];
        var dir = Path.Combine([.. segments.Where(s => s.Length > 0)]);
        Directory.CreateDirectory(dir);

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
        File.WriteAllText(
            Path.Combine(dir, page.Slug + ".md"),
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
        string outDir, string mediaDir,
        SortedDictionary<string, (string Orig, SortedSet<string> Refs, SortedSet<string> Alts)> mediaRefs)
    {
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
            var (sha1, bytes) = HashFile(localAbs);
            rows.Add(new[] { localPath, entry.Orig,
                string.Join(" | ", entry.Refs), string.Join(" | ", entry.Alts), sha1, bytes });
        }
        Csv.Write(Path.Combine(outDir, "media_manifest.csv"), rows);
    }

    private static (string Sha1, string Bytes) HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(SHA1.HashData(stream));
            return (hash, stream.Length.ToString());
        }
        catch (IOException)
        {
            return ("", "");
        }
    }

    private static void WriteRedirectMap(string outDir, List<(string OldUrl, string Locale, string TranslationKey)> redirects)
    {
        // old_url 收滿,new_path 待人工/後續階段補
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "old_url", "new_path", "lang", "translation_key" },
        };
        rows.AddRange(redirects.Select(r => new[] { r.OldUrl, "", r.Locale, r.TranslationKey }));
        Csv.Write(Path.Combine(outDir, "redirect_map.csv"), rows);
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
        int pagesWritten, int mediaReferenced, int missingCount, int needFetchCount, int suggestedPairs)
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
        };
    }
}
