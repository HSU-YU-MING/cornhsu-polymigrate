using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

public class PathSafetyTests
{
    [Theory]
    // §3.4:Windows 保留裝置名——含副檔名仍然保留(con.md 一樣寫不進去)
    [InlineData("content/ch/news/con.md")]
    [InlineData("content/ch/CON.md")]
    [InlineData("content/ch/aux.php.md")]
    [InlineData("content/COM3/x.md")]
    [InlineData("content/ch/lpt9.md")]
    public void ReservedDeviceNames_Rejected(string path) =>
        Assert.Contains("reserved", PathSafety.Check(path));

    [Theory]
    [InlineData("content/ch/a:b.md")]
    [InlineData("content/ch/what?.md")]
    [InlineData("content/ch/a|b.md")]
    public void InvalidCharacters_Rejected(string path) =>
        Assert.Contains("invalid character", PathSafety.Check(path));

    [Theory]
    // 其餘 Windows 禁用字元與控制字元(contracts.md「非法字元或控制字元」):Linux 寫得出來、
    // Windows 寫不出來的檔名,必須在每個平台都一樣地拒寫
    [InlineData("content/ch/a<b.md")]
    [InlineData("content/ch/a>b.md")]
    [InlineData("content/ch/a\"b.md")]
    [InlineData("content/ch/a\\b.md")]
    [InlineData("content/ch/a*b.md")]
    [InlineData("content/ch/tab\there.md")]
    [InlineData("content/ch/nul\0byte.md")]
    [InlineData("content/ch/del\u007f.md")]
    public void OtherWindowsInvalidAndControlCharacters_Rejected(string path) =>
        Assert.StartsWith("invalid character in ", PathSafety.Check(path));

    [Theory]
    [InlineData("content/ch./x.md", "ch.")]           // 目錄段尾點:Windows 會默默剝掉 → 路徑不一致
    [InlineData("content/ch /x.md", "ch ")]
    public void TrailingDotOrSpace_Rejected(string path, string segment)
    {
        var issue = PathSafety.Check(path);
        Assert.Contains("trailing", issue);
        Assert.Contains(segment, issue);
    }

    [Theory]
    [InlineData("content/ch/news/20260101.md")]
    [InlineData("content/ch/console.md")]     // console 不是保留字(只有 CON)
    [InlineData("content/ch/comet1.md")]      // comet1 ≠ COM1
    [InlineData("content/中文目錄/頁面.md")]
    public void SafePaths_Pass(string path) =>
        Assert.Null(PathSafety.Check(path));

    [Fact]
    public void CaseInsensitiveCollision_SecondPathRejected()
    {
        var seen = new Dictionary<string, string>();

        Assert.Null(PathSafety.RegisterOrCollide(seen, "content/ch/news/Page.md"));
        Assert.Null(PathSafety.RegisterOrCollide(seen, "content/ch/news/other.md"));

        var issue = PathSafety.RegisterOrCollide(seen, "content/ch/news/page.md");
        Assert.Contains("collision", issue);
        Assert.Contains("Page.md", issue);
    }

    [Fact]
    public void ExactDuplicateOutputPath_Rejected()
    {
        // 每個來源檔各呼叫一次,故第二次出現同一輸出路徑 = 兩個來源檔會互相覆蓋
        // (如 a.php.html 與 a.asp.html 都收斂成 a.md),必須記錄、拒寫,而不是靜默覆蓋
        var seen = new Dictionary<string, string>();

        Assert.Null(PathSafety.RegisterOrCollide(seen, "content/ch/news/a.md"));

        var issue = PathSafety.RegisterOrCollide(seen, "content/ch/news/a.md");
        Assert.Contains("duplicate", issue);
        Assert.Contains("a.md", issue);
    }

    [Fact]
    public void OverlongPath_IsWarningOnly()
    {
        Assert.Null(PathSafety.CheckLength(@"C:\out\" + new string('a', 100)));
        Assert.Contains("260", PathSafety.CheckLength(@"C:\out\" + new string('a', 300)));
    }

    [Fact]
    public void OverlongPath_BoundaryIsTheClassic259CharacterMaximum()
    {
        // 傳統 Windows MAX_PATH 260 含結尾的 NUL,路徑本身最多 259 字元:259 照常、260 起才 warning。
        // 這條 warning 會讓 extract 從 exit 0 變 1,差一個字元就是 exit code 契約上的差別
        Assert.Null(PathSafety.CheckLength(new string('a', 259)));
        Assert.Equal("path length 260 exceeds classic Windows limit (260)", PathSafety.CheckLength(new string('a', 260)));
    }
}
