using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Media;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core;

/// <summary>
/// PolyMigrate 函式庫的進入點(<c>Cornhsu.PolyMigrate.Core</c>)。
/// polymigrate CLI 是這層之上的殼;要在自己的 .NET 程式裡驅動搬遷,用這個型別即可,
/// 不必碰內部的 ExtractionPipeline / OutputVerifier 等實作型別。
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

    /// <summary>Phase 2 抽取,採預設佈局:<c>root/raw</c>、<c>root/media</c>,輸出至 <c>root</c>。</summary>
    public ExtractionReport Extract(string root, bool dryRun = false) =>
        Extract(ExtractionPaths.ForRoot(root), dryRun);

    /// <summary>縮圖:media 目錄 → 縮圖目錄(同結構、EXIF 轉正、寬度上限依 config)。</summary>
    public ThumbnailReport GenerateThumbnails(string mediaDir, string thumbnailDir) =>
        new ThumbnailGenerator(config).Run(mediaDir, thumbnailDir);

    /// <summary>
    /// 全站巡檢(Phase 4):只讀 Phase 2 輸出,不需 config 也不碰網路。
    /// </summary>
    /// <param name="outputDir">Phase 2 的輸出根目錄(內含 content/ 與清單)。</param>
    /// <param name="mediaDir">媒體目錄;null = <c>outputDir/media</c>(不存在則跳過媒體檢查)。</param>
    /// <param name="mediaPrefix">內文中媒體引用的根前綴。</param>
    public static VerifyReport Verify(string outputDir, string? mediaDir = null, string mediaPrefix = "/media/") =>
        new OutputVerifier().Run(outputDir, mediaDir ?? Path.Combine(outputDir, "media"), mediaPrefix);
}
