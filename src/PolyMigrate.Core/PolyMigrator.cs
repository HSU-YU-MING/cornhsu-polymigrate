using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Media;
using PolyMigrate.Core.Orphans;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core;

/// <summary>
/// PolyMigrate 函式庫的進入點(<c>Cornhsu.PolyMigrate.Core</c>)。
/// polymigrate CLI 是這層之上的殼;要在自己的 .NET 程式裡驅動抽取、縮圖、巡檢、列出已鏡像的 slug、
/// 探測與抓回孤兒頁,用這個型別即可(probe / fetch 入口與各方法的可取消 overload 自 3.0 起)。
/// <para>其餘 public 型別(ExtractionPipeline、OutputVerifier、ThumbnailGenerator、OrphanProber、
/// OrphanFetcher 等)是較底層的進階用途:它們同樣屬於公開面、受 semver 約束,只是沒有這一層的便利。</para>
/// </summary>
/// <example>
/// <code>
/// var migrator = PolyMigrator.FromConfigFile("site.yaml");
/// var report = migrator.Extract("out/");          // out/raw、out/media → out/content + 清單
/// if (report.HasErrors) { /* path_issues.csv 有 error */ }
/// var verify = PolyMigrator.Verify("out/");
/// </code>
/// </example>
public sealed class PolyMigrator(SiteConfig config)
{
    /// <summary>從 YAML config 檔建立(等同 CLI 的 <c>&lt;config.yaml&gt;</c> 參數;驗證失敗擲 <see cref="ConfigException"/>)。</summary>
    public static PolyMigrator FromConfigFile(string configPath) => new(SiteConfigLoader.LoadFile(configPath));

    /// <summary>Phase 2 結構化抽取(指定輸入/輸出位置)。</summary>
    /// <param name="paths">raw / media / 輸出位置。</param>
    /// <param name="dryRun">true = 完整跑抽取與統計但不寫任何檔案。</param>
    public ExtractionReport Extract(ExtractionPaths paths, bool dryRun = false) =>
        new ExtractionPipeline(config).Run(paths, dryRun);

    /// <summary>
    /// 同上,可取消:逐頁、逐個媒體檔(算 sha1 前)、開始寫清單前各檢查一次,取消時擲
    /// <see cref="OperationCanceledException"/>。已寫出的 <c>.md</c> 保留(只寫不刪);清單檔要嘛整組寫出、要嘛一份都不寫。
    /// </summary>
    public ExtractionReport Extract(ExtractionPaths paths, bool dryRun, CancellationToken ct) =>
        new ExtractionPipeline(config).Run(paths, dryRun, ct);

    /// <summary>Phase 2 抽取,採預設佈局:<c>root/raw</c>、<c>root/media</c>,輸出至 <c>root</c>。</summary>
    public ExtractionReport Extract(string root, bool dryRun = false) =>
        Extract(ExtractionPaths.ForRoot(root), dryRun);

    /// <summary>同上,可取消(檢查點見 <see cref="Extract(ExtractionPaths, bool, CancellationToken)"/>)。</summary>
    public ExtractionReport Extract(string root, bool dryRun, CancellationToken ct) =>
        Extract(ExtractionPaths.ForRoot(root), dryRun, ct);

    /// <summary>縮圖:media 目錄 → 縮圖目錄(同結構、EXIF 轉正、寬度上限依 config;動畫 GIF / WebP 逐格縮放)。</summary>
    public ThumbnailReport GenerateThumbnails(string mediaDir, string thumbnailDir) =>
        new ThumbnailGenerator(config).Run(mediaDir, thumbnailDir);

    /// <summary>
    /// 同上,可取消:每張圖之前檢查一次,取消時擲 <see cref="OperationCanceledException"/>。
    /// 已建立的縮圖保留(每張都是原子寫入,不會留半截檔)。
    /// </summary>
    public ThumbnailReport GenerateThumbnails(string mediaDir, string thumbnailDir, CancellationToken ct) =>
        new ThumbnailGenerator(config).Run(mediaDir, thumbnailDir, null, ct);

    /// <summary>
    /// 全站巡檢(Phase 4):只讀 Phase 2 輸出,不需 config 也不碰網路。
    /// </summary>
    /// <param name="outputDir">Phase 2 的輸出根目錄(內含 content/ 與清單)。</param>
    /// <param name="mediaDir">媒體目錄;null = <c>outputDir/media</c>(不存在則跳過媒體檢查)。</param>
    /// <param name="mediaPrefix">內文中媒體引用的根前綴。</param>
    /// <param name="allowUnlinked">豁免孤島偵測的頁(content 相對路徑或路由)。</param>
    public static VerifyReport Verify(string outputDir, string? mediaDir = null, string mediaPrefix = "/media/",
        IReadOnlyCollection<string>? allowUnlinked = null) =>
        new OutputVerifier().Run(outputDir, mediaDir ?? Path.Combine(outputDir, "media"), mediaPrefix, allowUnlinked);

    /// <summary>
    /// 同上,可取消:逐頁、寫報告前各檢查一次,取消時擲 <see cref="OperationCanceledException"/>,
    /// 不會寫出半份 <c>verify_report.csv</c>。參數意義與上面相同(這裡沒有預設值,避免與上一個多載互相模糊)。
    /// </summary>
    public static VerifyReport Verify(string outputDir, string? mediaDir, string mediaPrefix,
        IReadOnlyCollection<string>? allowUnlinked, CancellationToken ct) =>
        new OutputVerifier().Run(outputDir, mediaDir ?? Path.Combine(outputDir, "media"), mediaPrefix, allowUnlinked, ct);

    /// <summary>
    /// 鏡像裡已有的 slug(<c>raw/{lang}/{section}/</c> 檔名),字典序、跨語言去重;
    /// 純本地、不碰網路、不需 config。並行期用來跟舊站的現況求差集,
    /// 差集再餵給 <c>fetch-orphans --slugs</c>。
    /// </summary>
    /// <param name="rawDir">鏡像根目錄(<c>root/raw</c>)。</param>
    /// <param name="section">section 目錄名(如 news)。</param>
    /// <param name="langPrefix">語言前綴;null = 涵蓋所有語言目錄。</param>
    /// <returns>找不到任何對應目錄時為 null(通常是 section 名打錯)——與「有目錄但沒有頁」不同。</returns>
    public static IReadOnlyList<string>? MirrorSlugs(string rawDir, string section, string? langPrefix = null) =>
        Inventory.MirrorSlugs.List(rawDir, section, langPrefix);

    /// <summary>
    /// 探測孤兒頁(CLI 的 <c>probe-orphans</c>;索引移除但頁面還在的舊文章):逐日期候選 slug(YYYYMMDD 與 MMDDYYYY),
    /// 命中再探 A～D 後綴。與 CLI 同一套規則:已鏡像的 slug 取自 <c>root/raw/{lang}/{section}/</c>、依
    /// <c>url_pattern.strip_extensions</c> 判定(與 extract 一致;本身不探、後綴照探),禮貌延遲 <c>site.polite.delay_ms</c>,
    /// 送 <c>site.auth_workaround</c> 的 cookie,不跟轉址(轉址頁不算命中)。
    /// <para><b>不寫任何檔案</b>:清單在 <see cref="ProbeReport.Found"/>,要不要寫成 <c>orphan_slugs.&lt;section&gt;.txt</c>
    /// 由呼叫端決定。<see cref="ProbeReport.NoUsableAnswers"/> 為 true 時不要把空清單當成「沒有孤兒頁」
    /// (CLI 在這個情況 exit 2、不覆寫既有清單)。</para>
    /// </summary>
    /// <param name="root">站的工作根目錄(讀 <c>root/raw</c> 判斷哪些 slug 已鏡像)。</param>
    /// <param name="section">section 目錄名(如 news)。</param>
    /// <param name="yearFrom">起始年(含)。</param>
    /// <param name="yearTo">結束年(含)。</param>
    /// <param name="langPrefix">探哪個語言的網址;null = <c>url_pattern.lang_map</c> 的第一個 key(同 CLI 的 <c>--lang</c> 預設)。</param>
    /// <param name="progress">進度訊息(命中、每 100 個候選一次)。</param>
    /// <param name="ct">取消時擲 <see cref="OperationCanceledException"/>(等待中的延遲與進行中的請求都會被打斷)。</param>
    /// <exception cref="ArgumentOutOfRangeException">年份不在 1～9999,或 <paramref name="yearFrom"/> 大於 <paramref name="yearTo"/>。</exception>
    public async Task<ProbeReport> ProbeOrphansAsync(string root, string section, int yearFrom, int yearTo,
        string? langPrefix = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        EnsureYears(yearFrom, yearTo);   // 在建 HttpClient 與發任何請求之前
        using var http = OrphanProber.CreateHttpClient(config);
        return await ProbeOrphansAsync(http, root, section, yearFrom, yearTo, langPrefix, progress, ct);
    }

    /// <summary>同上,HttpClient 由呼叫端提供(測試注入用;public 版自己建、自己釋放)。</summary>
    internal Task<ProbeReport> ProbeOrphansAsync(HttpClient http, string root, string section, int yearFrom, int yearTo,
        string? langPrefix, Action<string>? progress, CancellationToken ct)
    {
        EnsureYears(yearFrom, yearTo);
        var lang = langPrefix ?? config.UrlPattern.LangMap.Keys.First();
        var known = OrphanProber.KnownSlugs(Path.Combine(root, "raw"), lang, section, config.UrlPattern.StripExtensions);
        return new OrphanProber(config, http).ProbeDetailedAsync(lang, section, yearFrom, yearTo, known, progress, ct);
    }

    /// <summary>
    /// 抓回孤兒頁(CLI 的 <c>fetch-orphans</c>):每個 slug 抓 <c>url_pattern.lang_map</c> 的每一種語言版,
    /// 頁面存進 <c>root/raw/</c>(原始 bytes)、正文引用的圖片與資產存進 <c>root/media/</c>(路徑鏡像、解碼檔名)。
    /// 已鏡像的頁跳過(只寫不刪);跟轉址(頁面可能在 301 之後),存檔名仍是要求的 slug。
    /// 單頁失敗(非 200、非 HTML、連線失敗、逾時)記進 <see cref="FetchReport.Errors"/>、不中止整批(CLI 在這個情況 exit 1)。
    /// </summary>
    /// <param name="root">站的工作根目錄(寫進 <c>root/raw</c> 與 <c>root/media</c>)。</param>
    /// <param name="section">section 目錄名(如 news)。</param>
    /// <param name="slugs">要抓的 slug(通常是 <see cref="ProbeOrphansAsync(string, string, int, int, string?, Action{string}?, CancellationToken)"/> 的 <see cref="ProbeReport.Found"/>)。</param>
    /// <param name="progress">進度訊息(每抓到一頁一則)。</param>
    /// <param name="ct">取消時擲 <see cref="OperationCanceledException"/>;已寫出的頁與資產保留(原子寫入,不會留半截檔)。</param>
    /// <exception cref="ConfigException"><c>extract.content</c> 不是合法的 CSS selector(在寫入抓到的頁之前就擲)。</exception>
    public async Task<FetchReport> FetchOrphansAsync(string root, string section, IReadOnlyList<string> slugs,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        using var http = OrphanProber.CreateHttpClient(config, allowRedirects: true);
        return await FetchOrphansAsync(http, root, section, slugs, progress, ct);
    }

    /// <summary>同上,HttpClient 由呼叫端提供(測試注入用;public 版自己建、自己釋放)。</summary>
    internal Task<FetchReport> FetchOrphansAsync(HttpClient http, string root, string section, IReadOnlyList<string> slugs,
        Action<string>? progress, CancellationToken ct) =>
        new OrphanFetcher(config, http).FetchAsync(
            slugs, section, Path.Combine(root, "raw"), Path.Combine(root, "media"), progress, ct);

    private static void EnsureYears(int yearFrom, int yearTo)
    {
        if (yearFrom is < 1 or > 9999 || yearTo is < 1 or > 9999 || yearFrom > yearTo)
        {
            throw new ArgumentOutOfRangeException(nameof(yearFrom),
                $"Years must be within 1-9999 with yearFrom <= yearTo, got {yearFrom}-{yearTo}.");
        }
    }
}
