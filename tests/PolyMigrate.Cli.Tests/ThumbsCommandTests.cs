using ImageMagick;

namespace PolyMigrate.Cli.Tests;

/// <summary>
/// <c>polymigrate thumbs</c>:0 = 全部成功或已存在、1 = 有圖失敗(壞檔不阻斷其餘)、2 = 用法/config/找不到媒體目錄。
/// 在此之前 CLI 層只測了「選項缺值」。
/// </summary>
public class ThumbsCommandTests : CliTestBase
{
    private void WriteJpeg(string relative, uint width = 20, uint height = 10)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(MagickColors.White, width, height);
        image.Write(path, MagickFormat.Jpeg);
    }

    [Fact]
    public async Task Success_ExitsZero_DefaultOutputIsMediaThumbBesideMedia()
    {
        var config = Write("site/site.yaml", Config());
        WriteJpeg("site/media/ch/news/a.jpg");

        var r = await Run("thumbs", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains("Thumbs done.", r.Out);
        Assert.Contains("  created : 1\n", r.Out);
        Assert.Contains("  failed  : 0\n", r.Out);
        Assert.True(File.Exists(PathOf("site/media_thumb/ch/news/a.jpg")));
    }

    [Fact]
    public async Task Cancelled_Returns130_WithoutCreatingThumbnails()
    {
        // 原本同步指令不看 token:第一次 Ctrl-C 沒作用、照常跑完;CLAUDE.md 與 CHANGELOG 2.2.0 卻說會乾淨回 130
        var config = Write("site/site.yaml", Config());
        WriteJpeg("site/media/a.jpg");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var r = await RunWith(cts.Token, "thumbs", config);

        Assert.Equal(130, r.Exit);
        Assert.Equal("cancelled.\n", r.Err);
        Assert.False(File.Exists(PathOf("site/media_thumb/a.jpg")));
    }

    [Fact]
    public async Task Rerun_SkipsExisting_ExitsZero()
    {
        var config = Write("site/site.yaml", Config());
        WriteJpeg("site/media/a.jpg");
        Assert.Equal(0, (await Run("thumbs", config)).Exit);

        var r = await Run("thumbs", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains("  created : 0\n", r.Out);
        Assert.Contains("  skipped : 1 (already exist)\n", r.Out);
    }

    [Fact]
    public async Task AnimationSummary_CountsKeptAndFirstFrame_AndListsWhy_WithoutChangingTheExitCode()
    {
        // 動畫退回第一格不是失敗(與 2.3.1 的結果相同),不算 warning、不影響 exit code;但摘要要講出來
        var config = Write("site/site.yaml", Config(extraTop: "media:\n  thumbnails: { max_width: 200 }"));
        var kept = PathOf("site/media/anim.gif");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        using (var animation = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Lime, MagickColors.Blue })
            {
                animation.Add(new MagickImage(color, 800, 400) { AnimationDelay = 20 });
            }
            animation.Write(kept, MagickFormat.Gif);
        }
        using (var bars = new MagickImageCollection())
        {
            for (var i = 0; i < 20; i++)
            {
                var frame = new MagickImage(MagickColors.White, 800, 400) { AnimationDelay = (uint)(10 + i) };
                frame.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(i * 36, 40, i * 36 + 24, 360));
                frame.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                bars.Add(frame);
            }
            bars.Write(PathOf("site/media/bars.webp"), MagickFormat.WebP);
        }

        var r = await Run("thumbs", config);

        Assert.Equal(0, r.Exit);
        Assert.Contains("  created : 2\n", r.Out);
        Assert.Contains("  animated: 1 kept, 1 as first frame\n", r.Out);
        Assert.Contains("  [first frame] bars.webp :: the animated thumbnail (", r.Out);
    }

    [Fact]
    public async Task DecompressionBomb_IsAFailure_ExitsOne_OthersStillDone()
    {
        // 小檔、巨大尺寸(70 位元組的 PNG 宣告 65535x65535):解碼前就擋下,與其他縮圖失敗一樣 exit 1、[fail] 列出原因。
        // 2.3.1 會整張解碼(20000x20000 實測 14.8 秒、1.6 GB),更大的直接拖垮機器
        var config = Write("site/site.yaml", Config());
        WriteJpeg("site/media/ok.jpg");
        var bomb = PathOf("site/media/bomb.png");
        File.WriteAllBytes(bomb, BombPng(65535, 65535));

        var r = await Run("thumbs", config);

        Assert.Equal(1, r.Exit);
        Assert.Contains("  created : 1\n", r.Out);
        Assert.Contains("  failed  : 1\n", r.Out);
        Assert.Contains("  [fail] bomb.png :: 65535x65535 (4294.8 MP) exceeds the 179 MP still-image limit\n", r.Out);
        Assert.False(File.Exists(PathOf("site/media_thumb/bomb.png")));
    }

    private static byte[] BombPng(int width, int height)
    {
        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }
            return ~crc;
        }
        static byte[] Chunk(string type, byte[] data)
        {
            byte[] typeAndData = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
            var crc = Crc32(typeAndData);
            return [(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
                .. typeAndData, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
        }
        byte[] ihdr = [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 1, 0, 0, 0, 0];
        return [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a,
            .. Chunk("IHDR", ihdr), .. Chunk("IDAT", [0x78, 0x9c, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]), .. Chunk("IEND", [])];
    }

    [Fact]
    public async Task CorruptImage_ExitsOne_OthersStillDone()
    {
        var config = Write("site/site.yaml", Config());
        WriteJpeg("site/media/ok.jpg");
        Write("site/media/sub/bad.jpg", "not a jpeg");

        var r = await Run("thumbs", config);

        Assert.Equal(1, r.Exit);
        Assert.Contains("  created : 1\n", r.Out);
        Assert.Contains("  failed  : 1\n", r.Out);
        Assert.Contains("  [fail] sub/bad.jpg :: ", r.Out);   // 相對路徑一律 '/' 分隔
        Assert.True(File.Exists(PathOf("site/media_thumb/ok.jpg")));
    }

    [Fact]
    public async Task MediaDirectoryMissing_ExitsTwo()
    {
        var config = Write("site/site.yaml", Config());

        var r = await Run("thumbs", config);

        Assert.Equal(2, r.Exit);
        Assert.Equal($"Media directory not found: {PathOf("site/media")}\n", r.Err);
    }

    [Fact]
    public async Task MediaAndOutOverrides_AreHonored()
    {
        var config = Write("cfg/site.yaml", Config());
        WriteJpeg("photos/a.jpg");

        var r = await Run("thumbs", config, "--media", PathOf("photos"), "--out", PathOf("small"));

        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(PathOf("small/a.jpg")));
        Assert.False(Directory.Exists(PathOf("cfg/media_thumb")));
    }

    [Fact]
    public async Task RootOverride_LocatesMediaAndDefaultOutput()
    {
        var config = Write("cfg/site.yaml", Config());
        WriteJpeg("work/media/a.jpg");

        var r = await Run("thumbs", config, "--root", PathOf("work"));

        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(PathOf("work/media_thumb/a.jpg")));
    }

    [Fact]
    public async Task ConfiguredMaxWidth_ReachesTheGenerator()
    {
        var config = Write("site/site.yaml", Config(extraTop: "media:\n  thumbnails:\n    max_width: 50"));
        WriteJpeg("site/media/wide.jpg", 400, 100);

        var r = await Run("thumbs", config);

        Assert.Equal(0, r.Exit);
        using var thumb = new MagickImage(PathOf("site/media_thumb/wide.jpg"));
        Assert.Equal((50u, 13u), (thumb.Width, thumb.Height));
    }

    [Fact]
    public async Task InvalidConfig_ExitsTwo()
    {
        var config = Write("site/site.yaml", Config(extraTop: "media:\n  thumbnails:\n    quality: 0"));
        WriteJpeg("site/media/a.jpg");

        var r = await Run("thumbs", config);

        Assert.Equal(2, r.Exit);
        Assert.Contains("media.thumbnails.quality must be 1-100, got 0.", r.Err);
        Assert.False(Directory.Exists(PathOf("site/media_thumb")));
    }

    [Theory]
    [InlineData("second.yaml")]
    [InlineData("--bogus")]
    public async Task UnexpectedArgument_ExitsTwo_WithUsage(string extra)
    {
        var r = await Run("thumbs", "site.yaml", extra);

        Assert.Equal(2, r.Exit);
        Assert.StartsWith($"Unexpected argument: {extra}\nUsage: polymigrate thumbs", r.Err);
    }

    [Fact]
    public async Task MissingConfigArgument_ExitsTwo_WithUsage()
    {
        var r = await Run("thumbs");

        Assert.Equal(2, r.Exit);
        Assert.StartsWith("Usage: polymigrate thumbs", r.Err);
    }
}
