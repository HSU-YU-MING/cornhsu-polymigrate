namespace PolyMigrate.Core.Configuration;

/// <summary>
/// 一站一份的 site config(規劃書 §2.5)。YAML 反序列化與 schema 驗證(§3.9)於 0.1 實作。
/// </summary>
public sealed class SiteConfig
{
    /// <summary>config 格式版本(§3.9)。目前僅支援 1。</summary>
    public int ConfigVersion { get; init; } = 1;

    public required SiteSection Site { get; init; }

    public required UrlPatternSection UrlPattern { get; init; }

    public required ExtractSection Extract { get; init; }

    public PairingSection Pairing { get; init; } = new();

    public MediaSection Media { get; init; } = new();
}

public sealed class SiteSection
{
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// 來源站編碼(§3.1)。null = Phase 0 自動偵測(header → meta → BOM → 統計)。
    /// </summary>
    public string? Encoding { get; init; }

    /// <summary>static | headless(需 JS 渲染才長出正文時)。</summary>
    public RenderMode Render { get; init; } = RenderMode.Static;

    public PoliteSection Polite { get; init; } = new();
}

public enum RenderMode
{
    Static,
    Headless,
}

public sealed class PoliteSection
{
    public int Concurrency { get; init; } = 1;

    public int DelayMs { get; init; } = 3000;
}

public sealed class UrlPatternSection
{
    /// <summary>
    /// URL 語言前綴 → BCP-47 標準 locale 的對映(§3.3)。
    /// 例:{ "ch": "zh-Hant", "en": "en" }。frontmatter 與清單一律輸出標準 locale;
    /// URL 前綴只是來源站的實作細節。單語站 = 只有一組對映的特例(§2.3)。
    /// </summary>
    public required IReadOnlyDictionary<string, string> LangMap { get; init; }

    /// <summary>預設語言,以 BCP-47 locale 表示(須為 LangMap 的值之一)。</summary>
    public required string DefaultLang { get; init; }
}

public sealed class ExtractSection
{
    /// <summary>正文的 CSS selector。</summary>
    public required string Content { get; init; }

    /// <summary>標題 selector,優先取內文標題而非 &lt;title&gt;(§2.6)。</summary>
    public string? Title { get; init; }

    /// <summary>標題要剝除的前綴 regex 清單(如日期前綴)。</summary>
    public IReadOnlyList<string> StripTitlePrefix { get; init; } = [];
}

public sealed class PairingSection
{
    public PairingStrategy Strategy { get; init; } = PairingStrategy.SymmetricPath;

    /// <summary>對稱配對失敗時的啟發式建議順序(§1.4)。</summary>
    public IReadOnlyList<string> Fallback { get; init; } = [];
}

public enum PairingStrategy
{
    /// <summary>去語言前綴的路徑當 translation_key(§1.4)。</summary>
    SymmetricPath,
}

public sealed class MediaSection
{
    public bool Download { get; init; } = true;

    public ThumbnailSection? Thumbnails { get; init; }
}

public sealed class ThumbnailSection
{
    public int MaxWidth { get; init; } = 1000;

    public int Quality { get; init; } = 82;
}
