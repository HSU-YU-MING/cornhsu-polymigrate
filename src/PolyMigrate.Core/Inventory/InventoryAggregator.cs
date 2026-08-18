using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Inventory;

/// <summary>一個 translation_key 聚合各語言版本後的中間紀錄(§1.4)。</summary>
internal sealed class InventoryRecord
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

/// <summary>media 相對路徑 → (原始 URL, 引用它的頁面集, alt 文字集)。</summary>
internal sealed record MediaEntry(string OriginalUrl, SortedSet<string> Refs, SortedSet<string> Alts);

/// <summary>一筆 301:原始 URL → 新路由,附語言與配對鍵。</summary>
internal sealed record Redirect(string OldUrl, string NewPath, string Locale, string TranslationKey);

/// <summary>
/// 一則 hreflang 宣告,補上兩端在本站的路由(§1.4)。
/// <paramref name="TargetRoute"/> = null 代表指到站外(見 <see cref="LinkRewriter.RouteForUrl"/>)。
/// </summary>
internal sealed record HreflangLink(
    string SourceUrl,
    string SourceRoute,
    string SourceKey,
    string SourceLocale,
    string Hreflang,
    string TargetUrl,
    string? TargetRoute);

/// <summary>
/// 逐頁把 <see cref="ExtractedPage"/> 摺疊成各清單的中間表示(以 translation_key 聚合各語言版本)。
/// 純記憶體、零 I/O —— 讓聚合邏輯不必落磁碟就能單獨測(對照 ExtractionPipeline 的全站 golden 測)。
/// </summary>
internal sealed class InventoryAggregator(LinkRewriter links)
{
    public SortedDictionary<string, InventoryRecord> Inventory { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, MediaEntry> MediaRefs { get; } = new(StringComparer.Ordinal);

    public List<MissingImage> Missing { get; } = [];

    public SortedSet<string> NeedFetch { get; } = new(StringComparer.Ordinal);

    public List<Redirect> Redirects { get; } = [];

    /// <summary>各頁宣告的 hreflang(原始觀察,未過濾);解析見 <see cref="Pairing.HreflangIndex"/>。</summary>
    public List<HreflangLink> HreflangLinks { get; } = [];

    /// <summary>站內路由 → translation_key。hreflang 指到的頁「在不在鏡像裡」就靠這張表回答。</summary>
    public Dictionary<string, string> RouteToKey { get; } = new(StringComparer.Ordinal);

    public void Add(RawPage page, ExtractedPage extracted)
    {
        if (!Inventory.TryGetValue(page.TranslationKey, out var record))
        {
            record = new InventoryRecord
            {
                Section = page.Section,
                Slug = page.Slug,
                Type = extracted.PageType,
                Title = extracted.Title,
            };
            Inventory[page.TranslationKey] = record;
        }
        record.TextByLocale[page.Locale] = extracted.TextLength;
        record.ImageCount = Math.Max(record.ImageCount, extracted.ImageCount);
        record.Flags.UnionWith(extracted.Flags);
        record.Media.UnionWith(extracted.MediaUses.Select(u => u.MediaRelative));

        foreach (var use in extracted.MediaUses)
        {
            if (!MediaRefs.TryGetValue(use.MediaRelative, out var entry))
            {
                entry = new MediaEntry(use.OriginalUrl,
                    new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                MediaRefs[use.MediaRelative] = entry;
            }
            entry.Refs.Add(use.SourceUrl);
            if (use.Alt.Length > 0)
            {
                entry.Alts.Add(use.Alt);
            }
        }
        Missing.AddRange(extracted.MissingImages);
        NeedFetch.UnionWith(extracted.NeedFetch);

        var route = links.RouteForPath(new Uri(page.SourceUrl).AbsolutePath);
        RouteToKey[route] = page.TranslationKey;
        HreflangLinks.AddRange(extracted.Alternates.Select(a => new HreflangLink(
            page.SourceUrl, route, page.TranslationKey, page.Locale,
            a.Hreflang, a.TargetUrl, links.RouteForUrl(a.TargetUrl))));

        Redirects.Add(new Redirect(page.SourceUrl, route, page.Locale, page.TranslationKey));
    }
}
