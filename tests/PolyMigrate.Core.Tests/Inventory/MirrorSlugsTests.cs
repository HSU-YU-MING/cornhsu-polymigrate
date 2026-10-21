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
    [InlineData("v1.2-release.php.html", "v1.2-release")]   // 原本取第一個點之前 → v1
    [InlineData("2019.05.01.php.html", "2019.05.01")]       // 原本 → 2019
    [InlineData("2019.05.01.html", "2019.05.01")]           // 沒有動態副檔名的乾淨網址:.01 不是副檔名
    [InlineData("v1.2-release.html", "v1.2-release")]
    [InlineData("about.asp.html", "about")]
    [InlineData("20260101.html", "20260101")]
    public void DottedSlug_StripsOnlyHtmlAndOneDynamicExtension_LikeExtract(string fileName, string slug)
    {
        // 與 extract 的 slug 同一把尺(RawPageParser 只剝 strip_extensions + .html):含點的 slug 原本被截在
        // 第一個點,餵給 fetch-orphans --slugs 會去抓不存在的網址
        AddMirrored("ch", "news", fileName);

        Assert.Equal([slug], PolyMigrator.MirrorSlugs(Raw, "news"));
    }

    [Theory]
    // 審查 minor 5:不帶 config 的啟發式(去 .html 再去一段全字母的副檔名)在這兩種站會與 extract 不一致——
    // 手上有 config 的呼叫端(probe-orphans)改用與 RawPageParser 完全相同的規則
    [InlineData("jane.doe.html", new[] { ".php" }, "jane.doe")]   // 乾淨網址站的 /people/jane.doe:啟發式會剝成 jane
    [InlineData("foo.php.html", new string[0], "foo.php")]        // strip_extensions: [] 時 .php 是 slug 的一部分
    [InlineData("a.php.html", new[] { ".php" }, "a")]
    [InlineData("a.asp.html", new[] { ".php" }, "a.asp")]          // 不在 strip_extensions 裡的副檔名不剝
    [InlineData("a.asp.html", new[] { ".php", ".asp" }, "a")]
    public void WithStripExtensions_UsesExactlyTheExtractRule(string fileName, string[] stripExtensions, string slug)
    {
        AddMirrored("ch", "news", fileName);

        Assert.Equal(new HashSet<string> { slug },
            PolyMigrate.Core.Inventory.MirrorSlugs.ForLanguage(Raw, "ch", "news", stripExtensions));
        var config = new PolyMigrate.Core.Configuration.SiteConfig
        {
            Site = new() { BaseUrl = "https://x.example" },
            UrlPattern = new() { LangMap = new() { ["ch"] = "zh-Hant" }, DefaultLang = "zh-Hant", StripExtensions = [.. stripExtensions] },
        };
        var page = new PolyMigrate.Core.Extraction.RawPageParser(config.Site, config.UrlPattern)
            .Parse(Raw, Path.Combine(Raw, "ch", "news", fileName));
        Assert.Equal(slug, page.Slug);   // 防呆:與 extract 實際算出的 slug 相同
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
    public void FileNameThatIsOnlyASuffix_NeverBecomesABlankLine()
    {
        // 目錄網址(/ch/news/)被鏡像成只剩尾綴的 .php.html:取主檔名會得到空字串,
        // 而 slugs 的 stdout 是直接導成檔案再餵給 fetch-orphans 的——空行就是一筆假 slug
        AddMirrored("ch", "news", "20260101.php.html", ".php.html", ".html");

        var slugs = PolyMigrator.MirrorSlugs(Raw, "news");

        Assert.NotNull(slugs);
        Assert.Contains("20260101", slugs);
        Assert.DoesNotContain("", slugs);
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
