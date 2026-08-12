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
