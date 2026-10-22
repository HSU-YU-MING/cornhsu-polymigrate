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
        // 這裡的 GIF 只有一格;多格的動畫見檔尾「動畫」那一組
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

    // ── 動畫(決策檔 L4 選項 A):逐格縮放、保留動畫 ─────────────────────────────
    // 2.3.1 以前以 new MagickImage(file) 讀檔,只讀第一格:動畫 GIF / WebP 的縮圖變成靜止的第一格,
    // 照樣計入 created、沒有任何提示。docs/搬完之後你還要做的事.md 也提醒過「動畫 GIF 被重新編碼會壞掉」。

    private static readonly MagickColor[] FrameColors = [MagickColors.Red, MagickColors.Lime, MagickColors.Blue];

    /// <summary>三格動畫,每格一種純色、間隔 0.2 秒(AnimationDelay 的單位是 1/100 秒)、無限循環。</summary>
    private void WriteAnimation(string relative, MagickFormat format, uint width, uint height)
    {
        using var animation = new MagickImageCollection();
        foreach (var color in FrameColors)
        {
            animation.Add(new MagickImage(color, width, height) { AnimationDelay = 20, AnimationIterations = 0 });
        }
        animation.Write(MediaPath(relative), format);
    }

    [Theory]
    [InlineData("anim.gif", MagickFormat.Gif)]
    [InlineData("anim.webp", MagickFormat.WebP)]
    public void AnimatedImage_KeepsEveryFrame_EachDownscaled(string name, MagickFormat format)
    {
        WriteAnimation(name, format, 2000, 500);

        var report = Run();

        Assert.Equal(1, report.Created);
        using var thumb = new MagickImageCollection(Path.Combine(OutDir, name));
        Assert.Equal(3, thumb.Count);
        Assert.Equal(format, thumb[0].Format);
        thumb.Coalesce();   // 讀回時補成完整畫面再比(寫出時可能做過格間差異最佳化)
        Assert.All(thumb, frame =>
        {
            Assert.Equal((1000u, 250u), (frame.Width, frame.Height));
            Assert.Equal(20u, frame.AnimationDelay);
        });
    }

    [Fact]
    public void AnimatedGif_FramesKeepTheirOwnContent()
    {
        // 逐格縮放,不是「第一格縮好再複製三次」:每格的顏色要對得上(GIF 對純色是無損的)
        WriteAnimation("anim.gif", MagickFormat.Gif, 2000, 500);

        Run();

        using var thumb = new MagickImageCollection(Path.Combine(OutDir, "anim.gif"));
        thumb.Coalesce();
        for (var i = 0; i < FrameColors.Length; i++)
        {
            using var pixels = thumb[i].GetPixels();
            Assert.Equal(FrameColors[i], pixels.GetPixel(500, 125).ToColor());
        }
    }

    [Fact]
    public void AnimatedGif_NarrowerThanMaxWidth_IsNotUpscaled_ButStaysAnimated()
    {
        WriteAnimation("small.gif", MagickFormat.Gif, 300, 200);

        Run();

        using var thumb = new MagickImageCollection(Path.Combine(OutDir, "small.gif"));
        Assert.Equal(3, thumb.Count);
        thumb.Coalesce();
        Assert.All(thumb, frame => Assert.Equal((300u, 200u), (frame.Width, frame.Height)));
    }

    [Fact]
    public void TruncatedAnimatedGif_StillGetsAFirstFrameThumbnail_LikeBefore()
    {
        // 舊站鏡像裡下載到一半的 GIF 很常見。多格讀取(MagickImageCollection)對截斷的檔直接擲例外,
        // 不退回單張路徑的話,這張圖就從「有第一格縮圖」(2.3.1)變成 failed、thumbs 由 exit 0 變 1(審查必修 1)。
        // 截斷的 WebP 單張也讀不起來(2.3.1 一樣是 failed),不在這條的範圍
        const string name = "cut.gif";
        WriteAnimation(name, MagickFormat.Gif, 2000, 500);
        var path = MediaPath(name);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length * 2 / 3)]);
        using (var probe = new MagickImage())
        {
            // 前提:這個截斷檔單張讀得起來(2.3.1 的路徑),多格讀不起來(3.0 新路徑)
            probe.Read(path);
            Assert.ThrowsAny<MagickException>(() => new MagickImageCollection(path));
        }

        var report = Run();

        Assert.Equal(0, report.Failed);
        Assert.Equal(1, report.Created);
        using var thumb = new MagickImageCollection(Path.Combine(OutDir, name));
        Assert.Equal(1000u, Assert.Single(thumb).Width);
    }

    [Fact]
    public void SingleFrameGif_StillOneFrame()
    {
        using (var image = new MagickImage(MagickColors.Red, 2000, 500))
        {
            image.Write(MediaPath("still.gif"), MagickFormat.Gif);
        }

        Run();

        using var thumb = new MagickImageCollection(Path.Combine(OutDir, "still.gif"));
        Assert.Equal((1000u, 250u), (Assert.Single(thumb).Width, thumb[0].Height));
    }
}
