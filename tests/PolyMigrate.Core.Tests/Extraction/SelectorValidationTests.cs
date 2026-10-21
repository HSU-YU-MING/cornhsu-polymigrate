using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>
/// config 裡的 CSS selector 寫壞時(載入時刻意不驗語法,見 <c>CssSelectors</c>):
/// 原本 AngleSharp 的 <c>DomException</c> 直接穿出去,CLI 以未處理例外結束(exit code 不在契約內),
/// 而且崩潰前的頁已經寫進 content/、清單檔一份都沒寫。
/// 現在是 <see cref="ConfigException"/>,訊息點名欄位,並且在寫任何檔之前就停。
/// </summary>
public class SelectorValidationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-selector").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WritePage(string relative, string body = "<p>內文</p>")
    {
        var path = Path.Combine(_root, "raw", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<html><body><section id=\"personal\">{body}</section></body></html>");
    }

    private ExtractionPaths Paths() => ExtractionPaths.ForRoot(_root);

    [Fact]
    public void InvalidContentSelector_ThrowsConfigException_NamingTheField_NothingWritten()
    {
        WritePage("ch/index.php.html");
        var config = TestConfigs.IbpsLike();
        config.Extract.Content = "section[id";

        var ex = Assert.Throws<ConfigException>(() => new ExtractionPipeline(config).Run(Paths()));

        Assert.Contains("extract.content", ex.Message);
        Assert.Contains("'section[id'", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "content")));
        Assert.False(File.Exists(Path.Combine(_root, "content_inventory.csv")));
    }

    [Theory]
    [InlineData(true)]    // title_selector
    [InlineData(false)]   // remove_selectors
    public void InvalidTypeRuleSelector_StopsBeforeTheFirstPageIsWritten(bool titleSelector)
    {
        // ch/index(page 型,字典序在前)不會用到 article 的規則;壞 selector 原本要等到
        // ch/news/a(article 型)才爆,那時 ch/index.md 已經寫出去了
        WritePage("ch/index.php.html");
        WritePage("ch/news/a.php.html");
        var config = TestConfigs.IbpsLike();
        var rules = config.Extract.TypeRules["article"];
        if (titleSelector)
        {
            rules.TitleSelector = "h3[[";
        }
        else
        {
            rules.RemoveSelectors = ["h5", "h5[["];
        }

        var ex = Assert.Throws<ConfigException>(() => PolyMigrator(config).Extract(Paths()));

        Assert.Contains(titleSelector
            ? "extract.type_rules.article.title_selector"
            : "extract.type_rules.article.remove_selectors", ex.Message);
        Assert.Contains(titleSelector ? "'h3[['" : "'h5[['", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "content")));
    }

    [Fact]
    public void InvalidSelectorInATypeWithNoPages_IsNotAnError()
    {
        // 1.0 起一直能跑的 config:寫壞但這次沒有任何頁用到的規則不擋(否則就是 YAML 面的破壞)
        WritePage("ch/index.php.html");
        var config = TestConfigs.IbpsLike();
        config.Extract.TypeRules["event"] = new PageTypeRules { TitleSelector = "h3[[", RemoveSelectors = ["(("] };

        var report = new ExtractionPipeline(config).Run(Paths());

        Assert.Equal(1, report.PagesWritten);
    }

    [Fact]
    public void EmptyMirror_NeverUsesTheContentSelector_SoItIsNotChecked()
    {
        // 一頁都沒有時 extract.content 從來不會被用到:維持原本的「跑完、0 頁」
        Directory.CreateDirectory(Path.Combine(_root, "raw"));
        var config = TestConfigs.IbpsLike();
        config.Extract.Content = "section[id";

        var report = new ExtractionPipeline(config).Run(Paths());

        Assert.Equal(0, report.PagesWritten);
    }

    private static PolyMigrate.Core.PolyMigrator PolyMigrator(SiteConfig config) => new(config);
}
