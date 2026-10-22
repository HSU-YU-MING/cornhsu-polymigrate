using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Media;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core;

/// <summary>
/// PolyMigrate 函式庫的進入點(<c>Cornhsu.PolyMigrate.Core</c>)。
/// polymigrate CLI 是這層之上的殼;要在自己的 .NET 程式裡驅動抽取、縮圖、巡檢與列出已鏡像的 slug,
/// 用這個型別即可。
/// <para>其餘 public 型別(ExtractionPipeline、OutputVerifier、ThumbnailGenerator、OrphanProber、
/// OrphanFetcher 等)是較底層的進階用途:它們同樣屬於公開面、受 semver 約束,只是沒有這一層的便利。
/// 探測與抓回孤兒頁(CLI 的 probe-orphans / fetch-orphans)目前沒有 facade 入口,
/// 要直接用 OrphanProber / OrphanFetcher。</para>
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
}
