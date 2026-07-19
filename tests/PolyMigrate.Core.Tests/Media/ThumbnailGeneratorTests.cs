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
