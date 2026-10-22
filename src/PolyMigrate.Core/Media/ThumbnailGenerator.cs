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

    /// <summary>保留動畫(逐格縮放)的多格 GIF / WebP 數。這些也算在 <see cref="Created"/> 裡。</summary>
    public int AnimationsKept { get; set; }

    /// <summary>
    /// 多格的 GIF / WebP 改用第一格的縮圖(與 2.3.1 的結果相同):(相對路徑, 原因)。原因是超過像素預算,
    /// 或保留動畫的縮圖會比原檔大。這些也算在 <see cref="Created"/> 裡,不是失敗,不影響 exit code。
    /// </summary>
    public List<(string Path, string Reason)> AnimationsAsFirstFrame { get; } = [];
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

    /// <summary>
    /// 動畫縮圖的像素預算預設值:格數 × 畫布寬 × 畫布高 = 5,000 萬像素。
    /// <para>逐格縮放要先把每一格補成完整畫面(Coalesce),記憶體與時間都跟「格數 × 畫布」成正比。審查實測:
    /// 400 格 1300x900(4.68 億像素)要 39 秒、峰值 3.2 GB,約每百萬像素 7 MB、0.08～0.2 秒;298 格 800x408 的真實錄影
    /// (9,700 萬像素)要 21 秒,縮圖 4.18 MB 比原檔 1.8 MB 還大。5,000 萬像素讓最壞情況停在約 350 MB、10 秒以內,
    /// 而一般的小動畫(480x270 × 100 格約 1,300 萬)照樣保留。超過的退回第一格(與 2.3.1 相同),摘要會列出。</para>
    /// </summary>
    internal const long DefaultAnimationPixelBudget = 50_000_000;

    /// <summary>超過就退回第一格的像素預算;測試用小值才不必真的產生 5,000 萬像素的動畫。</summary>
    internal long AnimationPixelBudget { get; init; } = DefaultAnimationPixelBudget;

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
                if (TryWriteAnimated(file, tmp, thumbs, out var firstFrameReason))
                {
                    report.AnimationsKept++;
                }
                else
                {
                    if (firstFrameReason is not null)
                    {
                        report.AnimationsAsFirstFrame.Add((relative.Replace('\\', '/'), firstFrameReason));
                    }
                    using var image = new MagickImage(file);
                    image.AutoOrient();   // EXIF 轉正,必須在縮放前
                    Downscale(image, thumbs);
                    if (Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg")
                    {
                        image.Quality = (uint)thumbs.Quality;
                    }
                    // 決定性輸出:Magick 寫 PNG 時預設帶 tIME(寫出的時間)與 date:create / date:modify(取自輸入檔的
                    // 修改時間)、date:timestamp(現在時間)文字 chunk,同一張圖跑兩次位元組就不一樣。這個 define 只有
                    // PNG 編碼器會讀,所以一律設——檔名是 .gif、內容其實是 PNG 的也照樣生效。JPEG / WebP / GIF 本來就是決定性的
                    image.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
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
    /// 多格讀取但只讀中繼資料(Ping:格數與畫布大小,不解碼像素),先決定要不要保留動畫——
    /// 不先 Ping 的話,超過預算的大動畫光是讀進來就要好幾 GB。讀不起來回 null(改走單張路徑)。
    /// </summary>
    private static MagickImageCollection? PingFrames(string file)
    {
        try
        {
            var frames = new MagickImageCollection();
            frames.Ping(file);
            return frames;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>
    /// 多格的 GIF / WebP:逐格縮放、保留動畫(格數、每格的延遲與循環次數),寫到 <paramref name="tmp"/>。
    /// 單格(或其他格式、讀不起來的)回 false、<paramref name="firstFrameReason"/> 為 null,由呼叫端走一般的單張路徑。
    /// 確定是多格、但決定不保留動畫時也回 false,並在 <paramref name="firstFrameReason"/> 寫明原因(呼叫端改用第一格):
    /// 格數 × 畫布超過 <see cref="AnimationPixelBudget"/>(審查實測 400 格要 39 秒、3.2 GB),
    /// 或保留動畫的縮圖比原檔大(縮圖的意義是更小;審查實測 298 格的錄影 1.8 MB → 4.18 MB)。
    /// <para>2.3.1 以前一律 <c>new MagickImage(file)</c>,只讀第一格:動畫縮圖變成靜止的第一格,而且沒有任何提示
    /// (決策檔 L4 選項 A)。先 <c>Coalesce</c>:GIF 的後續格常只存「跟上一格不同的那一塊」,不補成完整畫面就縮,
    /// 各格的位置與大小會對不上(Coalesce 之後每格的畫布都從 (0,0) 起,AutoOrient 與 Resize 會跟著轉、跟著縮,
    /// 所以不必再 ResetPage——實測加不加輸出逐位元相同)。GIF 寫出前再 <c>Optimize</c> 回格間差異,檔案才不會因為每格都是全畫面而暴增;
    /// WebP 的編碼器自己處理格間差異,不另外做。</para>
    /// </summary>
    private bool TryWriteAnimated(string file, string tmp, ThumbnailSection thumbs, out string? firstFrameReason)
    {
        firstFrameReason = null;
        if (Path.GetExtension(file).ToLowerInvariant() is not (".gif" or ".webp"))
        {
            return false;
        }
        using (var info = PingFrames(file))
        {
            if (info is not { Count: >= 2 })
            {
                return false;
            }
            var page = info[0].Page;
            var (width, height) = page.Width > 0 && page.Height > 0 ? (page.Width, page.Height) : (info[0].Width, info[0].Height);
            // 每格以「宣告的畫面」與「這一格實際的大小」取大的算:解碼與 Coalesce 的記憶體跟實際影格走。
            // 只看宣告的畫面會被騙——宣告 10x10、影格卻是 3000x3000 的 GIF,原本算成 150 × 100 像素照樣整檔解碼,
            // 審查實測峰值 5.2 GB(建議 1)
            var pixels = info.Sum(f => (long)Math.Max(width, f.Width) * Math.Max(height, f.Height));
            if (pixels > AnimationPixelBudget)
            {
                var maxWidth = Math.Max(width, info.Max(f => f.Width));
                var maxHeight = Math.Max(height, info.Max(f => f.Height));
                var size = maxWidth == width && maxHeight == height
                    ? $"of {width}x{height}"
                    : $"up to {maxWidth}x{maxHeight} (the file declares {width}x{height})";
                firstFrameReason = $"{info.Count} frames {size} exceed the animation budget of {AnimationPixelBudget} pixels";
                return false;
            }
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
        }
        if (format == MagickFormat.Gif)
        {
            frames.Optimize();
        }
        frames.Write(tmp, format);
        var animatedBytes = new FileInfo(tmp).Length;
        var originalBytes = new FileInfo(file).Length;
        if (animatedBytes > originalBytes)
        {
            File.Delete(tmp);
            firstFrameReason = $"the animated thumbnail ({animatedBytes} bytes) would be larger than the original ({originalBytes} bytes)";
            return false;
        }
        return true;
    }
}
