namespace PolyMigrate.Core.Configuration;

/// <summary>
/// 一站一份的 site config(規劃書 §2.5)。欄位涵蓋 phase2_extract.py 規格中所有站別硬編碼。
/// YAML 載入與驗證見 <see cref="SiteConfigLoader"/>(§3.9)。
/// </summary>
public sealed class SiteConfig
{
    /// <summary>config 格式版本(§3.9)。目前僅支援 1。</summary>
    public int ConfigVersion { get; set; } = 1;

    public SiteSection Site { get; set; } = new();

    public UrlPatternSection UrlPattern { get; set; } = new();

    public ExtractSection Extract { get; set; } = new();

    public PairingSection Pairing { get; set; } = new();

    public MediaSection Media { get; set; } = new();
}

public sealed class SiteSection
{
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// 來源站編碼(§3.1),如 "utf-8"、"big5"。null = 預設 UTF-8;Phase 0 自動偵測落地後改為偵測值。
    /// </summary>
    public string? Encoding { get; set; }

    /// <summary>static | headless(需 JS 渲染才長出正文時)。</summary>
    public RenderMode Render { get; set; } = RenderMode.Static;

    public PoliteSection Polite { get; set; } = new();
}

public enum RenderMode
{
    Static,
    Headless,
}

public sealed class PoliteSection
{
    public int Concurrency { get; set; } = 1;

    public int DelayMs { get; set; } = 3000;
}

public sealed class UrlPatternSection
{
    /// <summary>
    /// URL 語言前綴 → BCP-47 標準 locale 的對映(§3.3)。
    /// 例:{ "ch": "zh-Hant", "en": "en" }。frontmatter 與清單一律輸出標準 locale;
    /// URL 前綴只是來源站的實作細節。單語站 = 只有一組對映的特例(§2.3)。
    /// </summary>
    public Dictionary<string, string> LangMap { get; set; } = [];

    /// <summary>預設語言,以 BCP-47 locale 表示(須為 LangMap 的值之一)。無語言前綴的頁面歸此語言。</summary>
    public string DefaultLang { get; set; } = "";

    /// <summary>
    /// 來源站動態頁副檔名(如 ".php"),用於:鏡像檔名去尾綴、內文連結改寫成新路由。
    /// </summary>
    public List<string> StripExtensions { get; set; } = [".php"];
}

public sealed class ExtractSection
{
    /// <summary>正文的 CSS selector;選不到任何節點時退回 &lt;body&gt;(規格 extract_body)。</summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// 標題雜訊字串(站名等),僅在取 &lt;title&gt; 標籤時剝除——內文真標題可能合法以站名開頭,
    /// 不可剝(規格 clean_title 的 strip_site 兩態)。
    /// </summary>
    public List<string> TitleNoise { get; set; } = [];

    /// <summary>額外的標題前綴剝除 regex(內建已含常見「日期 新聞:」三式,見 TitleCleaner)。</summary>
    public List<string> StripTitlePrefix { get; set; } = [];

    /// <summary>section 目錄名 → page_type(如 news→article、events→event);
    /// 未列者為 "page";列名 section 的 index 頁為 "listing"(規格 classify)。</summary>
    public Dictionary<string, string> SectionTypes { get; set; } = [];

    /// <summary>依 page_type 的抽取規則(標題 selector、內文移除、圖片進相簿)。</summary>
    public Dictionary<string, PageTypeRules> TypeRules { get; set; } = [];

    /// <summary>section → 附加 flag(如 support→needs_rebuild)。</summary>
    public Dictionary<string, string> SectionFlags { get; set; } = [];

    /// <summary>正文純文字少於此、卻有圖 → 標 text_in_image(資訊可能鎖在圖裡)。</summary>
    public int TextInImageMaxLength { get; set; } = 80;
}

public sealed class PageTypeRules
{
    /// <summary>此頁型優先用的內文標題 selector(如文章 h3),取不到才退回 &lt;title&gt;。</summary>
    public string? TitleSelector { get; set; }

    /// <summary>抽完標題後自內文移除的 selector(模板另行顯示的重複日期/標題)。</summary>
    public List<string> RemoveSelectors { get; set; } = [];

    /// <summary>true = 內文圖片移入 frontmatter images 相簿(縮圖格+燈箱),內文不重複顯示。</summary>
    public bool ImagesToGallery { get; set; }
}

public sealed class PairingSection
{
    public PairingStrategy Strategy { get; set; } = PairingStrategy.SymmetricPath;

    /// <summary>對稱配對失敗時的啟發式建議順序(§1.4);MVP 後續實作。</summary>
    public List<string> Fallback { get; set; } = [];
}

public enum PairingStrategy
{
    /// <summary>去語言前綴的路徑當 translation_key(§1.4)。</summary>
    SymmetricPath,
}

public sealed class MediaSection
{
    public bool Download { get; set; } = true;

    /// <summary>媒體改寫後的根絕對網路路徑前綴。</summary>
    public string WebPrefix { get; set; } = "/media/";

    /// <summary>PDF 內嵌下載連結文字,依 locale;查無則 "View PDF"。</summary>
    public Dictionary<string, string> PdfLabels { get; set; } = [];

    public ThumbnailSection? Thumbnails { get; set; }
}

public sealed class ThumbnailSection
{
    public int MaxWidth { get; set; } = 1000;

    public int Quality { get; set; } = 82;
}
