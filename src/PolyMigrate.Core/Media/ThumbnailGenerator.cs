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
/// 否則縮圖會顛倒/側躺。動畫 GIF / WebP 逐格縮放、保留動畫(3.0 起;2.3.1 以前只剩第一格)。已存在的縮圖跳過(可重跑、增量)。
/// 影像庫用 Magick.NET(Apache 2.0)——ImageSharp 4.x 建置即要求授權金鑰,已棄用(§3.7)。
/// </summary>
public sealed class ThumbnailGenerator(SiteConfig config)
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    public ThumbnailReport Run(string mediaDir, string outDir, Action<string>? progress = null) =>
        Run(mediaDir, outDir, progress, CancellationToken.None);

    /// <summary>
    /// 同上,可取消(取消時擲 <see cref="OperationCanceledException"/>;CLI 接 Ctrl-C 後回 130)。
    /// 逐張檢查;每張縮圖本來就是原子寫入,中斷不會留下半截檔。3.0 起 public(2.x 只有 CLI 用得到)。
    /// </summary>
    public ThumbnailReport Run(string mediaDir, string outDir, Action<string>? progress, CancellationToken ct)
    {
        var thumbs = config.Media.Thumbnails ?? new ThumbnailSection();
        var report = new ThumbnailReport();

        var files = Directory.EnumerateFiles(mediaDir, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetRelativePath(mediaDir, f).Replace('\\', '/'), StringComparer.Ordinal);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(mediaDir, file);
            var target = Path.Combine(outDir, relative);
            if (File.Exists(target))
            {
                report.Skipped++;
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // 先寫暫存再原子改名:中途中斷(Ctrl-C、磁碟滿)不會在最終路徑留半截圖,
                // 否則重跑會因「檔案已存在」把壞縮圖當成已完成。暫存名沒有副檔名,
                // 故寫出時明確指定格式(否則 Magick 會依 .tmp 推測格式而寫錯)。
                var tmp = target + ".tmp";
                if (!TryWriteAnimated(file, tmp, thumbs))
                {
                    using var image = new MagickImage(file);
                    image.AutoOrient();   // EXIF 轉正,必須在縮放前
                    Downscale(image, thumbs);
                    if (Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg")
                    {
                        image.Quality = (uint)thumbs.Quality;
                    }
                    image.Write(tmp, image.Format);
                }
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

    /// <summary>
    /// 多格讀取;讀不起來回 null(呼叫端改走單張路徑)。截斷的檔(舊站鏡像裡下載到一半的 GIF 很常見)多格讀取
    /// 直接擲例外,單張讀取卻讀得到第一格——退回單張,結果與 2.3.1 相同(第一格縮圖、不算 failed);
    /// 單張也讀不起來的才是 failed(審查必修 1)。
    /// </summary>
    private static MagickImageCollection? ReadFrames(string file)
    {
        try
        {
            return new MagickImageCollection(file);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static void Downscale(IMagickImage<byte> image, ThumbnailSection thumbs)
    {
        if (image.Width > (uint)thumbs.MaxWidth)
        {
            image.FilterType = FilterType.Lanczos;
            image.Resize(new MagickGeometry((uint)thumbs.MaxWidth, 0));
        }
    }

    /// <summary>
    /// 多格的 GIF / WebP:逐格縮放、保留動畫(格數、每格的延遲與循環次數),寫到 <paramref name="tmp"/>。
    /// 單格(或其他格式)回 false,由呼叫端走一般的單張路徑。
    /// <para>2.3.1 以前一律 <c>new MagickImage(file)</c>,只讀第一格:動畫縮圖變成靜止的第一格,而且沒有任何提示
    /// (決策檔 L4 選項 A)。先 <c>Coalesce</c>:GIF 的後續格常只存「跟上一格不同的那一塊」,不補成完整畫面就縮,
    /// 各格的位置與大小會對不上。GIF 寫出前再 <c>Optimize</c> 回格間差異,檔案才不會因為每格都是全畫面而暴增;
    /// WebP 的編碼器自己處理格間差異,不另外做。</para>
    /// </summary>
    private static bool TryWriteAnimated(string file, string tmp, ThumbnailSection thumbs)
    {
        if (Path.GetExtension(file).ToLowerInvariant() is not (".gif" or ".webp"))
        {
            return false;
        }
        using var frames = ReadFrames(file);
        if (frames is not { Count: >= 2 })
        {
            return false;
        }
        var format = frames[0].Format;
        frames.Coalesce();
        foreach (var frame in frames)
        {
            frame.AutoOrient();
            Downscale(frame, thumbs);
            frame.ResetPage();   // 縮放後的畫布 = 格本身的大小(Coalesce 留下的是原圖的畫布)
        }
        if (format == MagickFormat.Gif)
        {
            frames.Optimize();
        }
        frames.Write(tmp, format);
        return true;
    }
}
