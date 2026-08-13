using PolyMigrate.Core;

namespace PolyMigrate.Core.Tests.Inventory;

public class MirrorSlugsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-slugs").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Raw => Path.Combine(_root, "raw");

    private void AddMirrored(string langPrefix, string section, params string[] fileNames)
    {
        var dir = Path.Combine(Raw, langPrefix, section);
        Directory.CreateDirectory(dir);
        foreach (var name in fileNames)
        {
            File.WriteAllText(Path.Combine(dir, name), "<html></html>");
        }
    }

    [Fact]
    public void ListsSlugsSortedAndDedupedAcrossLanguages()
    {
        AddMirrored("ch", "news", "20260214.php.html", "20260101.php.html", "index.php.html");
        AddMirrored("en", "news", "20260101.php.html", "02142026.php.html");

        var slugs = PolyMigrator.MirrorSlugs(Raw, "news");

        // 字典序、跨語言去重(20260101 兩邊都有,只出現一次)
        Assert.Equal(["02142026", "20260101", "20260214", "index"], slugs);
    }

    [Fact]
    public void LangPrefix_LimitsToThatLanguage()
    {
        AddMirrored("ch", "news", "20260101.php.html");
        AddMirrored("en", "news", "02142026.php.html");

        Assert.Equal(["02142026"], PolyMigrator.MirrorSlugs(Raw, "news", "en"));
    }

    [Fact]
    public void FileNameWithMultipleDots_TakesStemOnly()
    {
        AddMirrored("ch", "news", "20260101.php.html");

        Assert.Equal(["20260101"], PolyMigrator.MirrorSlugs(Raw, "news"));
    }

    [Theory]
    [InlineData("C:\\Windows")]     // Path.Combine 遇到絕對路徑會丟掉前面的 rawDir
    [InlineData("/etc")]
    [InlineData("../../elsewhere")]
    [InlineData("..")]
    public void RootedOrTraversingSegment_IsRejected(string bad)
    {
        // §3.4:同一個輸入在每個平台都要有相同結果。這幾筆**在三個平台上都必須被擋**——
        // 用 Path.IsPathRooted 判斷的話,"C:\Windows" 在 Linux/macOS 上會是 false 而放行,
        // 而這正是 CI 抓到的第一版寫法(本機只跑 Windows 看不出來)。
        AddMirrored("ch", "news", "20260101.php.html");

        // 不出聲地去讀鏡像目錄以外的地方,比報錯糟——與「section 打錯回錯誤而非空清單」同一個原則
        Assert.Throws<ArgumentException>(() => PolyMigrator.MirrorSlugs(Raw, "news", bad));
        Assert.Throws<ArgumentException>(() => PolyMigrator.MirrorSlugs(Raw, bad));
    }

    [Fact]
    public void EmptyLangPrefix_IsStillAllowed()
    {
        // 無語言前綴的站合法用空字串,別被上面的檢查一起擋掉
        Directory.CreateDirectory(Path.Combine(Raw, "news"));
        File.WriteAllText(Path.Combine(Raw, "news", "20260101.php.html"), "<html></html>");

        Assert.Equal(["20260101"], PolyMigrator.MirrorSlugs(Raw, "news", ""));
    }

    [Fact]
    public void OsJunkFiles_AreNotSlugs()
    {
        // 鏡像目錄被 Finder / 檔案總管開過就會長出這些。逐檔取名的話
        // .DS_Store → 空字串 slug(輸出第一行是空行)、Thumbs.db → 假文章「Thumbs」,
        // 而這個指令的賣點就是 stdout 可以直接導成檔案。只認 *.html 就都擋掉了。
        AddMirrored("ch", "news", "20260101.php.html", ".DS_Store", "Thumbs.db");

        Assert.Equal(["20260101"], PolyMigrator.MirrorSlugs(Raw, "news"));
    }

    [Fact]
    public void NoLanguagePrefixSite_IsFound()
    {
        // 無語言前綴的站(lang_map 的 key 是 ""):section 直接在 raw/ 底下。
        // 只掃語言目錄的話會把 raw/news 當成語言 "news" 再去找 raw/news/news,
        // 於是整個單語站一筆都找不到——這類站是明確支援的,不是邊角。
        Directory.CreateDirectory(Path.Combine(Raw, "news"));
        File.WriteAllText(Path.Combine(Raw, "news", "20260101.php.html"), "<html></html>");

        Assert.Equal(["20260101"], PolyMigrator.MirrorSlugs(Raw, "news"));
    }

    [Fact]
    public void UnknownSection_IsNull_NotEmpty()
    {
        // section 打錯必須跟「有這個 section、但一篇都沒有」分得開:靜靜回空清單
        // 會被讀成「舊站沒有新文章」,而那正是排程任務最要命的失敗方式
        AddMirrored("ch", "news", "20260101.php.html");

        Assert.Null(PolyMigrator.MirrorSlugs(Raw, "newz"));
        Assert.Null(PolyMigrator.MirrorSlugs(Raw, "news", "de"));
        Assert.Null(PolyMigrator.MirrorSlugs(Path.Combine(_root, "nope"), "news"));
    }

    [Fact]
    public void EmptySection_IsEmptyList_NotNull()
    {
        Directory.CreateDirectory(Path.Combine(Raw, "ch", "events"));

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(
            PolyMigrator.MirrorSlugs(Raw, "events")));
    }
}
