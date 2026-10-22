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

    /// <summary>PNG 的 chunk 型別與(文字 chunk 的)關鍵字,依出現順序。</summary>
    private static List<string> PngChunks(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var chunks = new List<string>();
        for (var i = 8; i + 8 <= bytes.Length;)
        {
            var length = (bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3];
            var type = System.Text.Encoding.ASCII.GetString(bytes, i + 4, 4);
            if (type == "tEXt")
            {
                var keyword = System.Text.Encoding.Latin1.GetString(bytes, i + 8, length);
                type += ":" + keyword[..keyword.IndexOf('\0')];
            }
            chunks.Add(type);
            i += 12 + length;
        }
        return chunks;
    }

    [Theory]
    [InlineData("chart.png", MagickFormat.Png)]
    [InlineData("chart.jpg", MagickFormat.Jpeg)]
    [InlineData("chart.webp", MagickFormat.WebP)]
    [InlineData("chart.gif", MagickFormat.Gif)]
    public void SameInput_SameSettings_SameBytes(string name, MagickFormat format)
    {
        // 縮圖要是決定性輸出:同一張輸入、同樣的設定,位元組必須相同(重跑、換機器、版控比對都靠這一點)。
        // Magick 寫 PNG 時預設帶 tIME(寫出的時間)與 date:create / date:modify(取自輸入檔的修改時間)、
        // date:timestamp(現在時間)三種文字 chunk:同一張圖跑兩次就不一樣,複製過的輸入(修改時間變了)也不一樣
        WriteNoisy(name, format);
        var source = MediaPath(name);
        var thumbPath = Path.Combine(OutDir, name);

        File.SetLastWriteTimeUtc(source, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Run();
        var first = File.ReadAllBytes(thumbPath);
        File.Delete(thumbPath);
        File.SetLastWriteTimeUtc(source, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc));   // 內容不變、只有修改時間變
        Run();

        Assert.Equal(first, File.ReadAllBytes(thumbPath));
        if (format == MagickFormat.Png)
        {
            Assert.DoesNotContain(PngChunks(thumbPath), c => c is "tIME" || c.StartsWith("tEXt:date:", StringComparison.Ordinal));
        }
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

    /// <summary>
    /// 真實 GIF 的樣子:第一格是整張畫布,之後每格只存變動的那一塊(局部格,帶位移),每格延遲不同,只循環 3 次。
    /// 上面的三格純色動畫每格都是整張畫布、延遲相同、無限循環——拿掉 Coalesce、把延遲或循環次數弄丟,它都看不出來
    /// (審查建議 6:mutation L4-2、L4-6、L4-7 都存活)。
    /// </summary>
    private void WritePartialFrameAnimation(string relative)
    {
        using var animation = new MagickImageCollection();
        animation.Add(new MagickImage(MagickColors.Red, 2000, 500)
        {
            AnimationDelay = 10,
            AnimationIterations = 3,
            GifDisposeMethod = GifDisposeMethod.None,
        });
        // 兩個局部格:400x200 的色塊,貼在畫布的 (800, 100);沒蓋到的地方沿用上一格(dispose none)
        foreach (var (color, delay) in new[] { (MagickColors.Lime, 20u), (MagickColors.Blue, 70u) })
        {
            animation.Add(new MagickImage(color, 400, 200)
            {
                AnimationDelay = delay,
                GifDisposeMethod = GifDisposeMethod.None,
                Page = new MagickGeometry(800, 100, 2000, 500),
            });
        }
        animation.Write(MediaPath(relative), MagickFormat.Gif);
    }

    [Fact]
    public void PartialFrameGif_IsCoalescedBeforeScaling_AndKeepsEachDelayAndTheLoopCount()
    {
        WritePartialFrameAnimation("partial.gif");
        using (var source = new MagickImageCollection(MediaPath("partial.gif")))
        {
            Assert.Equal(400u, source[1].Width);   // 前提:素材真的是局部格
        }

        var report = Run();

        Assert.Equal(1, report.Created);
        using var thumb = new MagickImageCollection(Path.Combine(OutDir, "partial.gif"));
        Assert.Equal(3, thumb.Count);
        Assert.Equal([10u, 20u, 70u], thumb.Select(f => f.AnimationDelay));   // 每格自己的延遲
        Assert.Equal(3u, thumb[0].AnimationIterations);                       // 循環次數照舊
        Assert.Equal((1000u, 250u), (thumb[0].Page.Width, thumb[0].Page.Height));   // 畫布跟著縮(ResetPage)
        thumb.Coalesce();
        // 色塊縮半後落在 (400..600, 50..150);色塊外沿用第一格的紅色。沒先 Coalesce 的話,局部格縮放時
        // 不知道自己在畫布的哪裡,色塊會跑到左上角
        MagickColor[] expected = [MagickColors.Red, MagickColors.Lime, MagickColors.Blue];
        for (var i = 0; i < 3; i++)
        {
            using var pixels = thumb[i].GetPixels();
            Assert.Equal(expected[i], pixels.GetPixel(500, 100).ToColor());
            Assert.Equal(MagickColors.Red, pixels.GetPixel(100, 200).ToColor());
        }
    }

    // ── 動畫的上限(審查建議 5)────────────────────────────────────────────────────
    // 實測:400 格 1300x900 要 39 秒、3.2 GB;298 格的真實錄影要 21 秒,縮圖 4.18 MB,原檔才 1.8 MB

    [Fact]
    public void AnimationOverThePixelBudget_GetsAFirstFrameThumbnail_AndIsCounted()
    {
        WriteAnimation("big.gif", MagickFormat.Gif, 2000, 500);   // 3 格 × 2000 × 500 = 300 萬像素
        var generator = new ThumbnailGenerator(TestConfigs.IbpsLike()) { AnimationPixelBudget = 2_000_000 };

        var report = generator.Run(MediaDir, OutDir);

        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.AnimationsKept);
        var (path, reason) = Assert.Single(report.AnimationsAsFirstFrame);
        Assert.Equal("big.gif", path);
        Assert.Equal("3 frames of 2000x500 exceed the animation budget of 2000000 pixels", reason);
        using var thumb = new MagickImageCollection(Path.Combine(OutDir, "big.gif"));
        Assert.Equal(1000u, Assert.Single(thumb).Width);   // 與 2.3.1 相同:第一格
    }

    public static TheoryData<int, int, int, int> LyingScreens => new()
    {
        { 10, 10, 2000, 500 },   // 宣告 10x10 的畫面,每格都是 2000x500
        { 1, 1, 1, 1 },          // 第一格 1x1(畫面也照它宣告),之後的格才是 2000x500
    };

    [Theory]
    [MemberData(nameof(LyingScreens))]
    public void FramesBiggerThanTheDeclaredScreen_AreBudgetedByTheirRealSize(int screenW, int screenH, int frame0W, int frame0H)
    {
        // 惡意或壞掉的 GIF:原本預算只拿宣告的畫面 × 格數算(3 × 10 × 10 = 300 像素),照樣整檔解碼——
        // 審查以宣告 10x10、150 格 3000x3000 的 GIF 實測峰值 5.2 GB(建議 1)。改用 Ping 拿到的每格實際尺寸
        File.WriteAllBytes(MediaPath("liar.gif"), RawGif.Build((screenW, screenH), [(frame0W, frame0H), (2000, 500), (2000, 500)]));
        var generator = new ThumbnailGenerator(TestConfigs.IbpsLike()) { AnimationPixelBudget = 1_500_000 };

        var report = generator.Run(MediaDir, OutDir);

        Assert.Equal(0, report.AnimationsKept);
        Assert.Equal(0, report.Failed);
        var (path, reason) = Assert.Single(report.AnimationsAsFirstFrame);
        Assert.Equal("liar.gif", path);
        Assert.Equal($"3 frames up to 2000x500 (the file declares {screenW}x{screenH}) exceed the animation budget of 1500000 pixels", reason);
    }

    [Fact]
    public void AnimationWithACorruptMiddleFrame_FallsBackToTheFirstFrame_NotAFailure()
    {
        // Ping 只讀影格的標頭,中間某格的 LZW 壞掉照樣讀得到格數;完整讀取才擲例外。ReadFrames 要接住、退回單張路徑
        // (與 2.3.1 一樣的第一格)——審查 mutation D1(ReadFrames 不再接例外)原本存活
        var path = MediaPath("broken.gif");
        File.WriteAllBytes(path, RawGif.Build((400, 200), [(400, 200), (400, 200), (400, 200)], corruptFrame: 1));
        using (var ping = new MagickImageCollection())
        {
            ping.Ping(path);
            Assert.Equal(3, ping.Count);                                            // 前提:Ping 讀得到三格
            Assert.ThrowsAny<MagickException>(() => new MagickImageCollection(path));   // 前提:完整讀取會擲例外
        }

        var report = Run();

        Assert.Equal(0, report.Failed);
        Assert.Equal(1, report.Created);
        Assert.Equal(0, report.AnimationsKept);
        var (file, reason) = Assert.Single(report.AnimationsAsFirstFrame);
        Assert.Equal("broken.gif", file);
        Assert.Equal("not every frame could be decoded", reason);
        using var thumb = new MagickImageCollection(Path.Combine(OutDir, "broken.gif"));
        Assert.Equal(400u, Assert.Single(thumb).Width);
    }

    [Fact]
    public void FirstFrameFallbackThatAlsoFails_IsOnlyAFailure()
    {
        // 第一格就壞了:決定改用第一格,單張讀取卻也失敗。原本先記進 AnimationsAsFirstFrame 才寫,
        // 同一個檔同時出現在 [fail] 與 [first frame](最終驗證建議 6)。只有寫成功的才算「用了第一格」
        var path = MediaPath("dead.gif");
        File.WriteAllBytes(path, RawGif.Build((400, 200), [(400, 200), (400, 200), (400, 200)], corruptFrame: 0));
        Assert.ThrowsAny<MagickException>(() => new MagickImage(path));   // 前提:單張也讀不起來

        var report = Run();

        Assert.Equal(1, report.Failed);
        Assert.Equal("dead.gif", Assert.Single(report.Failures).Path);
        Assert.Empty(report.AnimationsAsFirstFrame);
        Assert.Equal(0, report.Created);
    }

    [Fact]
    public void AnimationWithinTheBudget_IsKept_AndCounted()
    {
        WriteAnimation("small.gif", MagickFormat.Gif, 2000, 500);

        var report = Run();

        Assert.Equal(1, report.AnimationsKept);
        Assert.Empty(report.AnimationsAsFirstFrame);
    }

    [Fact]
    public void DefaultBudget_IsFiftyMegapixels()
    {
        // 上限值與理由寫在 CHANGELOG 3.0.0 與 README:改它要一起改
        Assert.Equal(50_000_000, new ThumbnailGenerator(TestConfigs.IbpsLike()).AnimationPixelBudget);
    }

    [Fact]
    public void AnimatedThumbnailLargerThanTheOriginal_FallsBackToTheFirstFrame()
    {
        // 無損 WebP 的純色長條動畫壓得極小;重新編碼的縮圖反而比原檔大(審查實測 anim-lossless.webp:1910 → 3474 bytes)。
        // 縮圖比原檔還大就失去縮圖的意義:改成第一格
        using (var animation = new MagickImageCollection())
        {
            for (var i = 0; i < 20; i++)
            {
                var frame = new MagickImage(MagickColors.White, 800, 400) { AnimationDelay = (uint)(10 + i) };
                frame.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(i * 36, 40, i * 36 + 24, 360));
                frame.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                animation.Add(frame);
            }
            animation.Write(MediaPath("bars.webp"), MagickFormat.WebP);
        }
        var original = new FileInfo(MediaPath("bars.webp")).Length;

        var report = RunWith(maxWidth: 200);

        var (path, reason) = Assert.Single(report.AnimationsAsFirstFrame);
        Assert.Equal("bars.webp", path);
        Assert.StartsWith("the animated thumbnail (", reason);
        Assert.EndsWith($" bytes) would be larger than the original ({original} bytes)", reason);
        using var thumb = new MagickImageCollection(Path.Combine(OutDir, "bars.webp"));
        Assert.Equal(200u, Assert.Single(thumb).Width);
        Assert.False(File.Exists(Path.Combine(OutDir, "bars.webp.tmp")));
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
