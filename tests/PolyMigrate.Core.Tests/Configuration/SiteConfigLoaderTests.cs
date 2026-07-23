using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Tests.Configuration;

public class SiteConfigLoaderTests
{
    private const string MinimalYaml =
        """
        config_version: 1
        site:
          base_url: https://www.example.org
        url_pattern:
          lang_map: { ch: zh-Hant, en: en }
          default_lang: zh-Hant
        extract:
          content: "main"
        """;

    [Fact]
    public void MinimalConfig_LoadsWithDefaults()
    {
        var config = SiteConfigLoader.Load(MinimalYaml);

        Assert.Equal("zh-Hant", config.UrlPattern.LangMap["ch"]);
        Assert.Equal([".php"], config.UrlPattern.StripExtensions);
        Assert.Equal("/media/", config.Media.WebPrefix);
        Assert.Equal(80, config.Extract.TextInImageMaxLength);
        Assert.Equal(3000, config.Site.Polite.DelayMs);
    }

    [Fact]
    public void UnknownField_Throws()
    {
        // §3.9:欄位打錯要報錯,不可默默忽略
        var ex = Assert.Throws<ConfigException>(() =>
            SiteConfigLoader.Load(MinimalYaml + "\nextrat:\n  content: x\n"));

        Assert.Contains("extrat", ex.Message);
    }

    [Fact]
    public void UnsupportedConfigVersion_Throws()
    {
        var yaml = MinimalYaml.Replace("config_version: 1", "config_version: 2");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml));
        Assert.Contains("config_version", ex.Message);
    }

    [Fact]
    public void RelativeBaseUrl_Throws()
    {
        var yaml = MinimalYaml.Replace("https://www.example.org", "not-a-url");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml));
        Assert.Contains("base_url", ex.Message);
    }

    [Fact]
    public void DefaultLangNotInLangMap_Throws()
    {
        var yaml = MinimalYaml.Replace("default_lang: zh-Hant", "default_lang: ja");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml));
        Assert.Contains("default_lang", ex.Message);
    }

    [Fact]
    public void MissingContentSelector_Throws()
    {
        // 不用跨行 Replace 改樣本——原始碼行尾在 CRLF checkout 下會讓比對失效(CI Windows 踩過)
        const string yaml =
            """
            config_version: 1
            site:
              base_url: https://www.example.org
            url_pattern:
              lang_map: { ch: zh-Hant, en: en }
              default_lang: zh-Hant
            extract: {}
            """;

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml));
        Assert.Contains("extract.content", ex.Message);
    }

    [Fact]
    public void UnknownEncoding_Throws()
    {
        var yaml = MinimalYaml.Replace("base_url: https://www.example.org",
            "base_url: https://www.example.org\n  encoding: not-an-encoding");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml));
        Assert.Contains("encoding", ex.Message);
    }

    [Fact]
    public void Big5Encoding_Resolves()
    {
        // §3.1:舊中文站常見 Big5,必須認得
        var yaml = MinimalYaml.Replace("base_url: https://www.example.org",
            "base_url: https://www.example.org\n  encoding: big5");

        var config = SiteConfigLoader.Load(yaml);
        Assert.Equal("big5", config.Site.Encoding);
        Assert.NotNull(TextEncodings.Resolve(config.Site.Encoding));
    }

    [Theory]
    // 數值/語意欄位越界不再默默下傳給編碼器/排程器,載入即報錯
    [InlineData("site:\n  base_url: https://www.example.org\n  polite:\n    delay_ms: -1", "delay_ms")]
    [InlineData("extract:\n  content: \"main\"\n  text_in_image_max_length: -5", "text_in_image_max_length")]
    [InlineData("media:\n  thumbnails:\n    max_width: 0", "max_width")]
    [InlineData("media:\n  thumbnails:\n    quality: 200", "quality")]
    public void OutOfRangeNumericFields_Throw(string overrideBlock, string field)
    {
        // 每個 top-level key 只出現一次:把要覆寫的區塊替換進基底,其餘沿用預設
        var topKey = overrideBlock.Split(':')[0];
        var lines = new[]
        {
            "config_version: 1",
            topKey == "site" ? overrideBlock : "site:\n  base_url: https://www.example.org",
            "url_pattern:\n  lang_map: { ch: zh-Hant, en: en }\n  default_lang: zh-Hant",
            topKey == "extract" ? overrideBlock : "extract:\n  content: \"main\"",
            topKey == "media" ? overrideBlock : "",
        };
        var yaml = string.Join('\n', lines.Where(l => l.Length > 0));

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void EmptyLangMapLocale_Throws()
    {
        var yaml = MinimalYaml.Replace("lang_map: { ch: zh-Hant, en: en }", "lang_map: { ch: zh-Hant, en: \"\" }");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml));
        Assert.Contains("lang_map", ex.Message);
    }

    [Fact]
    public void ExampleConfig_IsValid()
    {
        var path = Path.Combine(FindRepoRoot(), "examples", "ibps-austin.yaml");

        var config = SiteConfigLoader.LoadFile(path);

        Assert.Equal("https://www.ibps-austin.org", config.Site.BaseUrl);
        Assert.True(config.Extract.TypeRules["article"].ImagesToGallery);
        Assert.Equal("下載／檢視 PDF", config.Media.PdfLabels["zh-Hant"]);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PolyMigrate.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        }
        return dir;
    }
}
