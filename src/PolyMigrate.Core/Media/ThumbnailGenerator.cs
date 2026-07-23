using ImageMagick;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Media;

public sealed class ThumbnailReport
{
    public int Created { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    /// <summary>(相對路徑, 錯誤訊息);縮圖失敗不阻斷(原圖可能截斷/損壞)。</summary>
    public List<(string Path, string Error)> Failures { get; } = [];
}

/// <summary>
/// 縮圖產生(規格 make_thumbs.py):media/ 圖片 → 縮圖目錄(同結構、同檔名)。
/// §2.6 坑:手機直拍照片 EXIF 方向顛倒 → 先依 EXIF 轉正(AutoOrient)再縮,
/// 否則縮圖會顛倒/側躺。已存在的縮圖跳過(可重跑、增量)。
/// 影像庫用 Magick.NET(Apache 2.0)——ImageSharp 4.x 建置即要求授權金鑰,已棄用(§3.7)。
/// </summary>
public sealed class ThumbnailGenerator(SiteConfig config)
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    public ThumbnailReport Run(string mediaDir, string outDir, Action<string>? progress = null)
    {
        var thumbs = config.Media.Thumbnails ?? new ThumbnailSection();
        var report = new ThumbnailReport();

        var files = Directory.EnumerateFiles(mediaDir, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetRelativePath(mediaDir, f).Replace('\\', '/'), StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(mediaDir, file);
            var target = Path.Combine(outDir, relative);
            if (File.Exists(target))
            {
                report.Skipped++;
                continue;
            }
            try
            {
                using var image = new MagickImage(file);
                image.AutoOrient();   // EXIF 轉正,必須在縮放前
                if (image.Width > (uint)thumbs.MaxWidth)
                {
                    image.FilterType = FilterType.Lanczos;
                    image.Resize(new MagickGeometry((uint)thumbs.MaxWidth, 0));
                }
                if (Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg")
                {
                    image.Quality = (uint)thumbs.Quality;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // 先寫暫存再原子改名:中途中斷(Ctrl-C、磁碟滿)不會在最終路徑留半截圖,
                // 否則重跑會因「檔案已存在」把壞縮圖當成已完成。暫存名沒有副檔名,
                // 故明確指定格式(否則 Magick 會依 .tmp 推測格式而寫錯)。
                var tmp = target + ".tmp";
                image.Write(tmp, image.Format);
                File.Move(tmp, target, overwrite: true);
                report.Created++;
                if (report.Created % 200 == 0)
                {
                    progress?.Invoke($"...{report.Created} done");
                }
            }
            catch (Exception ex) when (ex is MagickException or IOException)
            {
                report.Failed++;
                report.Failures.Add((relative.Replace('\\', '/'), ex.Message));
            }
        }
        return report;
    }
}
