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

    [Theory]
    // 未知欄位報錯不只在最上層:巢狀區段裡打錯一個字一樣要報錯並點名,
    // 否則那一項設定靜靜地沒生效(例如 polite 的間隔退回預設、縮圖寬度退回 1000)
    [InlineData("site:\n  base_url: https://www.example.org\n  polite:\n    delay: 0", "delay")]
    [InlineData("site:\n  base_url: https://www.example.org\n  auth_workaround:\n    type: cookie\n    cookies: { a: b }", "cookies")]
    [InlineData("url_pattern:\n  lang_map: { ch: zh-Hant }\n  default_lang: zh-Hant\n  strip_extension: [.asp]", "strip_extension")]
    [InlineData("extract:\n  content: main\n  type_rules:\n    article:\n      title_selectr: h3", "title_selectr")]
    [InlineData("media:\n  thumbnails:\n    width: 640", "width")]
    public void UnknownNestedField_Throws_NamingTheField(string overrideBlock, string field)
    {
        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(WithBlock(overrideBlock), "site.yaml"));

        Assert.StartsWith("site.yaml: invalid config — ", ex.Message);
        Assert.Contains($"'{field}'", ex.Message);
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

    // ─────────────────────────────────────────────────────────────────────────
    // YAML 欄位名是對外契約(1.0 起凍結)。欄位名由 C# 屬性名經 UnderscoredNamingConvention
    // 推導——屬性一改名,YAML 欄位名就跟著變,而載入器對未知欄位報錯,於是使用者既有的
    // config 載入失敗。下面這份把**每一個**欄位都用 YAML 名稱明寫一次並逐欄斷言,
    // 任何一個屬性被改名都會在這裡變紅(只靠 MinimalYaml 的話,沒寫到的欄位改名不會有人知道)。
    // ─────────────────────────────────────────────────────────────────────────

    private const string EveryFieldYaml =
        """
        config_version: 1
        site:
          base_url: https://old.example.org
          encoding: big5
          polite:
            delay_ms: 250
          auth_workaround:
            type: cookie
            set: { humans_21909: "1", lang: zh }
          user_agent: PolyMigrateTest/1.0
        url_pattern:
          lang_map: { en: en, ch: zh-Hant }
          default_lang: zh-Hant
          strip_extensions: [.asp, .php]
        extract:
          content: "#main"
          title_noise: [Example Temple]
          title_markers: [Nouvelles]
          strip_title_prefix: ['^\[\w+\]\s*']
          section_types: { news: article }
          type_rules:
            article:
              title_selector: "#main h3"
              remove_selectors: ["#main h5"]
              images_to_gallery: true
          section_flags: { support: needs_rebuild }
          text_in_image_max_length: 40
        pairing:
          fallback: [date, hreflang]
        media:
          web_prefix: /assets/
          pdf_labels: { en: Download PDF }
          thumbnails:
            max_width: 640
            quality: 70
        """;

    [Fact]
    public void EveryField_LoadsByItsYamlName()
    {
        var c = SiteConfigLoader.Load(EveryFieldYaml);

        Assert.Equal(1, c.ConfigVersion);

        Assert.Equal("https://old.example.org", c.Site.BaseUrl);
        Assert.Equal("big5", c.Site.Encoding);
        Assert.Equal(250, c.Site.Polite.DelayMs);
        Assert.NotNull(c.Site.AuthWorkaround);
        Assert.Equal("cookie", c.Site.AuthWorkaround.Type);
        Assert.Equal(new Dictionary<string, string> { ["humans_21909"] = "1", ["lang"] = "zh" },
            c.Site.AuthWorkaround.Set);
        Assert.Equal("PolyMigrateTest/1.0", c.Site.UserAgent);

        // lang_map 的順序有意義:第一個 key 是 probe-orphans --lang 的預設,
        // 值的順序是 content_inventory.csv 各語言欄的順序
        Assert.Equal(["en", "ch"], c.UrlPattern.LangMap.Keys);
        Assert.Equal(["en", "zh-Hant"], c.UrlPattern.LangMap.Values);
        Assert.Equal("zh-Hant", c.UrlPattern.DefaultLang);
        Assert.Equal([".asp", ".php"], c.UrlPattern.StripExtensions);

        Assert.Equal("#main", c.Extract.Content);
        Assert.Equal(["Example Temple"], c.Extract.TitleNoise);
        Assert.Equal(["Nouvelles"], c.Extract.TitleMarkers);
        Assert.Equal([@"^\[\w+\]\s*"], c.Extract.StripTitlePrefix);
        Assert.Equal("article", c.Extract.SectionTypes["news"]);
        var rules = c.Extract.TypeRules["article"];
        Assert.Equal("#main h3", rules.TitleSelector);
        Assert.Equal(["#main h5"], rules.RemoveSelectors);
        Assert.True(rules.ImagesToGallery);
        Assert.Equal("needs_rebuild", c.Extract.SectionFlags["support"]);
        Assert.Equal(40, c.Extract.TextInImageMaxLength);

        Assert.Equal(["date", "hreflang"], c.Pairing.Fallback);   // 順序 = 權重

        Assert.Equal("/assets/", c.Media.WebPrefix);
        Assert.Equal("Download PDF", c.Media.PdfLabels["en"]);
        Assert.NotNull(c.Media.Thumbnails);
        Assert.Equal(640, c.Media.Thumbnails.MaxWidth);
        Assert.Equal(70, c.Media.Thumbnails.Quality);
    }

    [Fact]
    public void OmittedOptionalFields_TakeDocumentedDefaults()
    {
        var c = SiteConfigLoader.Load(MinimalYaml);

        Assert.Null(c.Site.Encoding);                        // null = UTF-8
        Assert.Null(c.Site.AuthWorkaround);
        Assert.Contains("Mozilla/5.0", c.Site.UserAgent);
        Assert.Equal(["新聞", "News"], c.Extract.TitleMarkers);
        Assert.Empty(c.Extract.TitleNoise);
        Assert.Empty(c.Extract.StripTitlePrefix);
        Assert.Empty(c.Pairing.Fallback);                    // hreflang 等啟發式預設全關(CLAUDE.md「地雷」)
        Assert.Null(c.Media.Thumbnails);                     // thumbs 改用 1000 / 82
    }

    [Fact]
    public void OmittedConfigVersion_DefaultsToOne()
    {
        var yaml = string.Join('\n', MinimalYaml.Replace("\r", "").Split('\n').Skip(1));

        Assert.Equal(1, SiteConfigLoader.Load(yaml).ConfigVersion);
    }

    /// <summary>以基底 config 為準,把某個 top-level 區段整段換掉(每個 top-level key 只出現一次)。</summary>
    private static string WithBlock(string overrideBlock)
    {
        var topKey = overrideBlock.Split(':')[0];
        string[] blocks =
        [
            "config_version: 1",
            topKey == "site" ? overrideBlock : "site:\n  base_url: https://www.example.org",
            topKey == "url_pattern" ? overrideBlock : "url_pattern:\n  lang_map: { ch: zh-Hant, en: en }\n  default_lang: zh-Hant",
            topKey == "extract" ? overrideBlock : "extract:\n  content: \"main\"",
            topKey == "pairing" ? overrideBlock : "",
            topKey == "media" ? overrideBlock : "",
        ];
        return string.Join('\n', blocks.Where(b => b.Length > 0));
    }

    [Theory]
    [InlineData("site:\n  base_url: ftp://files.example.org",
        "site.base_url must be an absolute http(s) URL, got 'ftp://files.example.org'.")]
    [InlineData("site:\n  base_url: https://www.example.org\n  auth_workaround:\n    type: header",
        "site.auth_workaround.type 'header' is not supported (only 'cookie').")]
    [InlineData("url_pattern:\n  lang_map: {}\n  default_lang: zh-Hant",
        "url_pattern.lang_map must map at least one URL prefix to a BCP-47 locale")]
    [InlineData("url_pattern:\n  lang_map: { ch: zh-Hant }\n  default_lang: ''",
        "url_pattern.default_lang is required.")]
    [InlineData("url_pattern:\n  lang_map: { ch: zh-Hant }",
        "url_pattern.default_lang is required.")]
    [InlineData("pairing:\n  fallback: [shared_media, url_similarity]",
        "pairing.fallback 'url_similarity' is not a known heuristic")]
    [InlineData("media:\n  thumbnails:\n    quality: 0",
        "media.thumbnails.quality must be 1-100, got 0.")]
    public void ValidationErrors_NameTheFieldAndTheProblem(string overrideBlock, string expected)
    {
        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(WithBlock(overrideBlock), "site.yaml"));

        Assert.StartsWith("site.yaml: config is invalid:", ex.Message);
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    // 邊界值本身合法:驗證寫成 < 而不是 <= 這件事要被釘住
    [InlineData("site:\n  base_url: http://www.example.org\n  polite:\n    delay_ms: 0")]
    [InlineData("extract:\n  content: \"main\"\n  text_in_image_max_length: 0")]
    [InlineData("media:\n  thumbnails:\n    max_width: 1\n    quality: 1")]
    [InlineData("media:\n  thumbnails:\n    quality: 100")]
    [InlineData("pairing:\n  fallback: [hreflang, slug_normalized, shared_media, date, title_similarity]")]
    public void BoundaryValues_AreAccepted(string overrideBlock) =>
        Assert.NotNull(SiteConfigLoader.Load(WithBlock(overrideBlock)));

    [Fact]
    public void SeveralProblems_AreAllReportedInOneMessage()
    {
        // 一次列完,而不是修一個、重跑、再看到下一個
        var yaml = WithBlock("site:\n  base_url: not-a-url\n  polite:\n    delay_ms: -1");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml, "x.yaml"));

        Assert.Equal(
            "x.yaml: config is invalid:\n  - site.base_url must be an absolute http(s) URL, got 'not-a-url'."
            + "\n  - site.polite.delay_ms must be >= 0, got -1.",
            ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# 只有註解\n")]
    public void EmptyConfig_SaysSo(string yaml)
    {
        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml, "empty.yaml"));

        Assert.Equal("empty.yaml: config is empty.", ex.Message);
    }

    [Theory]
    [InlineData("site: [unclosed")]                 // YAML 語法錯
    [InlineData("config_version: abc")]             // 型別不符
    public void MalformedYaml_IsConfigException_NotARawParserError(string yaml)
    {
        // CLI 只把 ConfigException 當成「印訊息、exit 2」;漏成 YamlException 會變未處理例外
        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml, "bad.yaml"));

        Assert.StartsWith("bad.yaml: invalid config — ", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void LoadFile_PrefixesMessagesWithThePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polymigrate-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, WithBlock("site:\n  base_url: not-a-url"));
        try
        {
            var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.LoadFile(path));
            Assert.StartsWith($"{path}: config is invalid:", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFile_MissingFile_IsConfigException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"polymigrate-{Guid.NewGuid():N}.yaml");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.LoadFile(path));

        Assert.Equal($"Config file not found: {path}", ex.Message);
    }

    // ── CSS selector 在載入時就驗(3.0 起)──────────────────────────────────────
    // 2.x 只在「真的要用」之前才驗:type_rules 裡寫壞、但這次沒有頁屬於那個頁型的 selector 照樣能跑,
    // thumbs / probe-orphans 根本不碰 selector,寫壞的 config 也照跑。那只是為了不讓原本能跑的 config
    // 在 patch 版開始失敗;寫壞的 config 不該等到某個指令、某個頁型才炸。

    private static string WithExtract(string extract) =>
        "config_version: 1\nsite:\n  base_url: https://www.example.org\n"
        + "url_pattern:\n  lang_map: { ch: zh-Hant, en: en }\n  default_lang: zh-Hant\n"
        + "extract:\n" + extract;

    [Theory]
    [InlineData("  content: \"section[id\"\n", "extract.content: 'section[id' is not a valid CSS selector.")]
    [InlineData("  content: \"main\"\n  type_rules:\n    article:\n      title_selector: \"h3[[\"\n",
        "extract.type_rules.article.title_selector: 'h3[[' is not a valid CSS selector.")]
    [InlineData("  content: \"main\"\n  type_rules:\n    article:\n      remove_selectors: [\"h5\", \"h5[[\"]\n",
        "extract.type_rules.article.remove_selectors: 'h5[[' is not a valid CSS selector.")]
    // 沒有任何 section 對應到 event 頁型:2.x 永遠不會用到、所以永遠不報;現在載入就擋
    [InlineData("  content: \"main\"\n  type_rules:\n    event:\n      title_selector: \"((\"\n",
        "extract.type_rules.event.title_selector: '((' is not a valid CSS selector.")]
    public void InvalidSelector_FailsAtLoad_NamingTheFieldAndTheSelector(string extract, string message)
    {
        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(WithExtract(extract), "site.yaml"));

        Assert.Equal("site.yaml: config is invalid:\n  - " + message, ex.Message);
    }

    [Fact]
    public void EveryInvalidSelector_IsListed_NotJustTheFirst()
    {
        var yaml = WithExtract(
            "  content: \"main[\"\n  type_rules:\n    article:\n      title_selector: \"h3[[\"\n"
            + "      remove_selectors: [\"ok\", \"h5[[\", \"((\"]\n");

        var ex = Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml, "site.yaml"));

        Assert.Equal(
            "site.yaml: config is invalid:\n"
            + "  - extract.content: 'main[' is not a valid CSS selector.\n"
            + "  - extract.type_rules.article.title_selector: 'h3[[' is not a valid CSS selector.\n"
            + "  - extract.type_rules.article.remove_selectors: 'h5[[' is not a valid CSS selector.\n"
            + "  - extract.type_rules.article.remove_selectors: '((' is not a valid CSS selector.",
            ex.Message);
    }

    [Fact]
    public void ValidSelectors_StillLoad()
    {
        var config = SiteConfigLoader.Load(WithExtract(
            "  content: \"section[id]:not(#header) > div, main\"\n  type_rules:\n    article:\n"
            + "      title_selector: \"#personal h3, .personal_1 h3\"\n      remove_selectors: [\"#personal h5\", \"p:first-child\"]\n"));

        Assert.Equal("#personal h3, .personal_1 h3", config.Extract.TypeRules["article"].TitleSelector);
    }

    [Fact]
    public void FixtureConfig_IsValid()
    {
        // golden 測試用的 fixture config 也要過載入時的 selector 檢查
        var config = SiteConfigLoader.LoadFile(Path.Combine(FindRepoRoot(), "tests", "fixtures", "site", "config.yaml"));

        Assert.Equal("#main h3", config.Extract.TypeRules["article"].TitleSelector);
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
