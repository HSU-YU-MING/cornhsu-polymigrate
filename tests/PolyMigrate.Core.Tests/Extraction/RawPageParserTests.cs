using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

public class RawPageParserTests
{
    private static readonly RawPageParser Parser = new(TestConfigs.IbpsLike());

    private static RawPage Parse(string relative) =>
        Parser.Parse(@"C:\raw", Path.Combine(@"C:\raw", relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void NewsArticle_FullIdentity()
    {
        var page = Parse("ch/news/20260712.php.html");

        Assert.Equal("ch", page.LangPrefix);
        Assert.Equal("zh-Hant", page.Locale);
        Assert.Equal("news", page.Section);
        Assert.Equal("20260712", page.Slug);
        Assert.Equal("https://www.ibps-austin.org/ch/news/20260712.php", page.SourceUrl);
        Assert.Equal("news/20260712", page.TranslationKey);
    }

    [Fact]
    public void SymmetricPaths_ShareTranslationKey()
    {
        // 雙語配對核心(§1.4):去語言前綴後同 key
        Assert.Equal(Parse("ch/news/20260712.php.html").TranslationKey,
                     Parse("en/news/20260712.php.html").TranslationKey);
    }

    [Fact]
    public void LanguageRootIndex_HasEmptySection()
    {
        var page = Parse("en/index.php.html");

        Assert.Equal("en", page.Locale);
        Assert.Equal("", page.Section);
        Assert.Equal("index", page.Slug);
        Assert.Equal("index", page.TranslationKey);
    }

    [Fact]
    public void PlainHtmlSuffix_AlsoStripped()
    {
        var page = Parse("ch/about.html");

        Assert.Equal("about", page.Slug);
        Assert.Equal("https://www.ibps-austin.org/ch/about", page.SourceUrl);
    }

    [Fact]
    public void UnprefixedPath_FallsBackToDefaultLang_WithSiteLevelKey()
    {
        var page = Parse("news/x.php.html");

        Assert.Equal("", page.LangPrefix);
        Assert.Equal("zh-Hant", page.Locale);
        Assert.Equal("news", page.Section);
        Assert.Equal("/news/x", page.TranslationKey);
    }

    [Fact]
    public void RootLanguageChooser_DoesNotPairWithLocalizedIndex()
    {
        // 語言選擇頁不是任何語言版的翻譯,key 不可與 ch/en 的 index 相撞
        Assert.NotEqual(Parse("ch/index.php.html").TranslationKey,
                        Parse("index.php.html").TranslationKey);
    }
}
