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
        Assert.Equal(RenderMode.Static, config.Site.Render);
        Assert.Equal(PairingStrategy.SymmetricPath, config.Pairing.Strategy);
        Assert.Equal([".php"], config.UrlPattern.StripExtensions);
        Assert.Equal("/media/", config.Media.WebPrefix);
        Assert.Equal(80, config.Extract.TextInImageMaxLength);
    }

    [Fact]
    public void UnderscoredEnum_Parses()
    {
        var config = SiteConfigLoader.Load(MinimalYaml + "\npairing:\n  strategy: symmetric_path\n");

        Assert.Equal(PairingStrategy.SymmetricPath, config.Pairing.Strategy);
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
