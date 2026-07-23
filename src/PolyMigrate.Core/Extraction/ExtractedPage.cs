namespace PolyMigrate.Core.Extraction;

/// <summary>frontmatter images 相簿的一筆(web 路徑 + alt)。</summary>
internal sealed record ImageRef(string Web, string Alt);

/// <summary>media 引用(供 media_manifest 聚合):media 相對路徑、原始 URL、引用頁、alt。</summary>
internal sealed record MediaUse(string MediaRelative, string OriginalUrl, string SourceUrl, string Alt);

/// <summary>頁面引用但鏡像中不存在的圖 = 原站即 404(§2.6),記錄不阻斷。</summary>
internal sealed record MissingImage(string SourcePage, string WebPath);

/// <summary>單頁抽取結果(規格 main 迴圈單檔部分的全部產物)。</summary>
internal sealed class ExtractedPage
{
    public required RawPage Page { get; init; }

    public required string Title { get; init; }

    public required string PageType { get; init; }

    public required List<string> Flags { get; init; }

    public required int TextLength { get; init; }

    public required int ImageCount { get; init; }

    public required string BodyMarkdown { get; init; }

    public required List<ImageRef> Images { get; init; }

    /// <summary>frontmatter videos:{type: youtube, url: …} 或 {type: local, src: …}。</summary>
    public required List<Dictionary<string, string>> Videos { get; init; }

    /// <summary>frontmatter documents:{src: web 路徑, orig: 原始 URL}。</summary>
    public required List<Dictionary<string, string>> Documents { get; init; }

    public required List<MediaUse> MediaUses { get; init; }

    public required List<MissingImage> MissingImages { get; init; }

    /// <summary>待補下載的本地影片/PDF 原始 URL(爬蟲當初沒抓 &lt;source&gt;/&lt;iframe pdf&gt;)。</summary>
    public required List<string> NeedFetch { get; init; }
}
