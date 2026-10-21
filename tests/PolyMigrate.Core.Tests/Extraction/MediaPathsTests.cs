using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

public class MediaPathsTests
{
    [Theory]
    // 正常資產:解碼後的相對路徑(§2.6:磁碟存解碼檔名)
    [InlineData("https://h/media/images/x.jpg", "media/images/x.jpg")]
    [InlineData("https://h/media/2020/a%20b.jpg", "media/2020/a b.jpg")]   // %20 → 空白
    [InlineData("https://h/deep/dir/photo.thumb.jpg", "deep/dir/photo.thumb.jpg")]
    public void NormalUrls_DecodedToRelative(string url, string expected) =>
        Assert.Equal(expected, MediaPaths.RelativeFromUrl(url));

    [Theory]
    // §3.4:編碼後的路徑穿越——AbsolutePath 只正規化字面 ../,%2f / %2e 會逃過,
    // 解碼後才還原成 ../ 逃出 media 根目錄。一律拒絕(回 null → 當缺圖記錄,不寫根外)。
    [InlineData("https://h/media/..%2f..%2fsecret.png")]
    [InlineData("https://h/media/%2e%2e%2fsecret.png")]
    [InlineData("https://h/media/a/..%2f..%2f..%2fetc/passwd")]
    [InlineData("https://h/media/..%5c..%5cwin.ini")]          // 反斜線分隔(Windows)
    public void EncodedTraversal_Rejected(string url) =>
        Assert.Null(MediaPaths.RelativeFromUrl(url));

    [Theory]
    // media.web_prefix 寫不寫結尾斜線都一樣;每段單次編碼(空白 → %20、中日韓 → UTF-8 百分號),'/' 保留
    [InlineData("/assets", "img/a b.jpg", "/assets/img/a%20b.jpg")]
    [InlineData("/assets/", "img/a b.jpg", "/assets/img/a%20b.jpg")]
    [InlineData("/media/", "ch/禪修.jpg", "/media/ch/%E7%A6%AA%E4%BF%AE.jpg")]
    public void WebPath_UsesConfiguredPrefix_SingleEncodedPerSegment(string prefix, string relative, string expected) =>
        Assert.Equal(expected, new MediaPaths(new PolyMigrate.Core.Configuration.MediaSection { WebPrefix = prefix }).WebPath(relative));

    [Fact]
    public void RootPath_IsNull() =>
        Assert.Null(MediaPaths.RelativeFromUrl("https://h/"));

    [Fact]
    public void LocalPath_StaysUnderRoot_ForAcceptedRelative()
    {
        var rel = MediaPaths.RelativeFromUrl("https://h/media/img/x.jpg");
        Assert.NotNull(rel);
        var root = Path.Combine(Path.GetTempPath(), "pm-media");
        var local = Path.GetFullPath(MediaPaths.LocalPath(root, rel));
        Assert.StartsWith(Path.GetFullPath(root), local);
    }
}
