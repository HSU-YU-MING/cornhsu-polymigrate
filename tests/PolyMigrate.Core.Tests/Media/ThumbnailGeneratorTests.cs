using ImageMagick;
using PolyMigrate.Core.Media;

namespace PolyMigrate.Core.Tests.Media;

public class ThumbnailGeneratorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-thumbs").FullName;
    private string MediaDir => Path.Combine(_root, "media");
    private string OutDir => Path.Combine(_root, "media_thumb");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MediaPath(string relative)
    {
        var path = Path.Combine(MediaDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private void WriteJpeg(string relative, uint width, uint height, ushort? exifOrientation = null)
    {
        using var image = new MagickImage(MagickColors.White, width, height);
        if (exifOrientation is { } o)
        {
            // ImageMagick 寫檔時以 image.Orientation 同步 EXIF profile 的方向值,兩者須同設才會持久化
            image.Orientation = (OrientationType)o;
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, o);
            image.SetProfile(exif);
        }
        image.Write(MediaPath(relative), MagickFormat.Jpeg);
    }

    private ThumbnailReport Run() => new ThumbnailGenerator(TestConfigs.IbpsLike()).Run(MediaDir, OutDir);

    [Fact]
    public void ExifRotatedImage_IsUprightInThumbnail()
    {
        // §2.6:手機直拍照片 EXIF Orientation=6(RightTop,需右轉 90°)→ 縮圖必須轉正
        WriteJpeg("ch/news/images/phone.jpg", 40, 20, exifOrientation: 6);

        var report = Run();

        Assert.Equal(1, report.Created);
        using var thumb = new MagickImage(Path.Combine(OutDir, "ch", "news", "images", "phone.jpg"));
        Assert.Equal((20u, 40u), (thumb.Width, thumb.Height));   // 40x20 轉正後 20x40
        Assert.True(thumb.Orientation is OrientationType.TopLeft or OrientationType.Undefined);
    }

    [Fact]
    public void WideImage_DownscaledToMaxWidth_KeepingAspect()
    {
        WriteJpeg("big.jpg", 2000, 500);

        Run();

        using var thumb = new MagickImage(Path.Combine(OutDir, "big.jpg"));
        Assert.Equal((1000u, 250u), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void SmallImage_NotUpscaled()
    {
        WriteJpeg("small.jpg", 300, 200);

        Run();

        using var thumb = new MagickImage(Path.Combine(OutDir, "small.jpg"));
        Assert.Equal((300u, 200u), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void ExistingThumbnail_SkippedOnRerun()
    {
        WriteJpeg("a.jpg", 10, 10);

        Assert.Equal(1, Run().Created);
        var rerun = Run();

        Assert.Equal(0, rerun.Created);
        Assert.Equal(1, rerun.Skipped);
    }

    /// <summary>有細節的照片型影像:JPEG 品質對檔案大小與量化表的影響才看得出來。</summary>
    private void WriteNoisy(string relative, MagickFormat format)
    {
        using var image = new MagickImage("plasma:", 400, 300);
        image.Write(MediaPath(relative), format);
    }

    private ThumbnailReport RunWith(int? maxWidth = null, int? quality = null, Action<string>? progress = null)
    {
        var config = TestConfigs.IbpsLike();
        if (maxWidth is not null || quality is not null)
        {
            config.Media.Thumbnails = new() { MaxWidth = maxWidth ?? 1000, Quality = quality ?? 82 };
        }
        return new ThumbnailGenerator(config).Run(MediaDir, OutDir, progress);
    }

    [Theory]
    [InlineData(null, 82)]   // config 沒寫 thumbnails 區段 → 預設 82
    [InlineData(10, 10)]
    [InlineData(95, 95)]
    public void JpegThumbnail_UsesConfiguredQuality(int? configured, int expected)
    {
        WriteNoisy("photo.jpg", MagickFormat.Jpeg);

        RunWith(quality: configured);

        // ImageMagick 讀 JPEG 時由量化表反推品質——寫進去的就是讀回來的
        using var thumb = new MagickImage(Path.Combine(OutDir, "photo.jpg"));
        Assert.Equal((uint)expected, thumb.Quality);
    }

    [Fact]
    public void Quality_AppliesToJpegOnly_PngOutputUnchanged()
    {
        // media.thumbnails.quality 只套 JPEG(ThumbnailGenerator 刻意如此):對 PNG 而言
        // Magick 的 Quality 是 zlib 壓縮等級 + 濾波器,套上去會讓同一張 PNG 隨 config 改變位元組
        WriteNoisy("chart.png", MagickFormat.Png);
        var thumbPath = Path.Combine(OutDir, "chart.png");

        RunWith(quality: 10);
        var low = new FileInfo(thumbPath).Length;
        File.Delete(thumbPath);
        RunWith(quality: 95);
        var high = new FileInfo(thumbPath).Length;

        Assert.Equal(low, high);
    }

    [Fact]
    public void ConfiguredMaxWidth_IsTheCap()
    {
        WriteJpeg("wide.jpg", 800, 400);

        RunWith(maxWidth: 200);

        using var thumb = new MagickImage(Path.Combine(OutDir, "wide.jpg"));
        Assert.Equal((200u, 100u), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void Progress_ReportedEvery200Created()
    {
        // 數千張圖的站,沒有進度就看不出是在跑還是卡住;但也不能每張都印(洗版)
        for (var i = 0; i < 401; i++)
        {
            WriteJpeg($"p/{i:000}.jpg", 1, 1);
        }
        var messages = new List<string>();

        var report = RunWith(progress: messages.Add);

        Assert.Equal(401, report.Created);
        Assert.Equal(["...200 done", "...400 done"], messages);
    }

    [Fact]
    public void ExtensionsMatchedCaseInsensitively_AndOutputMirrorsLayout()
    {
        WriteJpeg("ch/news/UPPER.JPG", 10, 10);
        WriteJpeg("ch/news/b.jpeg", 10, 10);
        using (var png = new MagickImage(MagickColors.Red, 10, 10))
        {
            png.Write(MediaPath("logo.png"), MagickFormat.Png);
        }

        var report = Run();

        Assert.Equal(3, report.Created);
        Assert.True(File.Exists(Path.Combine(OutDir, "ch", "news", "UPPER.JPG")));
        Assert.True(File.Exists(Path.Combine(OutDir, "ch", "news", "b.jpeg")));
        Assert.True(File.Exists(Path.Combine(OutDir, "logo.png")));
        // 原子寫入的暫存檔不得留在輸出裡
        Assert.Empty(Directory.GetFiles(OutDir, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("logo.png", MagickFormat.Png)]
    [InlineData("still.gif", MagickFormat.Gif)]
    [InlineData("photo.webp", MagickFormat.WebP)]
    public void Thumbnail_KeepsTheSourceFormat(string name, MagickFormat format)
    {
        // 縮圖與原圖同檔名,內容就必須是同一種格式:重新編碼時不可把 PNG/GIF/WebP 統一轉成別種
        // (JPEG 那一半見 LeftoverTmpFromAnInterruptedRun_DoesNotCountAsDone)。
        // 這裡的 GIF 只有一格。多格的 GIF 動畫目前會被縮成只剩第一格,那是待決的產品行為
        // (保留動畫、原樣複製、或刻意只取第一格),決定之前不寫測試把現況釘成規格
        using (var image = new MagickImage(MagickColors.Red, 1200, 10))
        {
            image.Write(MediaPath(name), format);
        }

        var report = Run();

        Assert.Equal(1, report.Created);
        using var thumb = new MagickImage(Path.Combine(OutDir, name));
        Assert.Equal(format, thumb.Format);
        Assert.Equal(1000u, thumb.Width);   // 有縮(重新編碼過),不是把原檔照抄過去
    }

    [Fact]
    public void LeftoverTmpFromAnInterruptedRun_DoesNotCountAsDone()
    {
        // 原子寫入的理由(CLAUDE.md「已知教訓」):中斷只會留下 .tmp,不會在最終路徑留半截圖,
        // 所以重跑時這張圖仍要被產生,而不是因為「有東西在」被跳過
        WriteJpeg("a.jpg", 10, 10);
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "a.jpg.tmp"), "half-written");

        var report = Run();

        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.Skipped);
        using var thumb = new MagickImage(Path.Combine(OutDir, "a.jpg"));
        Assert.Equal(MagickFormat.Jpeg, thumb.Format);
        Assert.False(File.Exists(Path.Combine(OutDir, "a.jpg.tmp")));
    }

    [Fact]
    public void NonImagesAndCorruptFiles_HandledWithoutBlocking()
    {
        File.WriteAllText(MediaPath("doc.pdf"), "not an image");        // 副檔名不在清單 → 忽略
        File.WriteAllText(MediaPath("corrupt.jpg"), "not a real jpeg"); // 壞檔 → failed,不阻斷
        WriteJpeg("ok.jpg", 10, 10);

        var report = Run();

        Assert.Equal(1, report.Created);
        Assert.Equal(1, report.Failed);
        Assert.Equal("corrupt.jpg", Assert.Single(report.Failures).Path);
    }
}
