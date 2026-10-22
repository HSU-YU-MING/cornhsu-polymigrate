using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Tests.Configuration;

/// <summary>
/// 3.0 起「寫壞的 config 不該等到某個指令才炸」:<see cref="SiteConfig"/> 每個欄位逐一看過,凡是載入時就驗得出來的
/// (regex、URL、路徑形狀、列舉值、數值範圍、YAML 寫了 key 卻沒給值),一律在載入時擋下,一次列出全部、點名欄位。
/// 2.x 這些不是到某個指令、某一頁才擲例外(regex 編譯失敗、null 造成的 NullReferenceException——後者連 exit code 都不在契約內),
/// 就是安靜地產出錯的東西(strip_extensions 少了點、web_prefix 不是根路徑、section 名稱含斜線永遠對不上…)。
/// 用到時的檢查保留為第二道防線(不經 loader、直接在程式裡組的 config)。
/// </summary>
public class ConfigLoadValidationTests
{
    private static string Yaml(string site = "", string polite = "", string urlPattern = "", string extract = "",
        string tail = "") =>
        "config_version: 1\n"
        + "site:\n  base_url: https://www.example.org\n" + site
        + (polite.Length > 0 ? "  polite:\n" + polite : "")
        + "url_pattern:\n  lang_map: { ch: zh-Hant, en: en }\n  default_lang: zh-Hant\n" + urlPattern
        + "extract:\n  content: \"main\"\n" + extract
        + tail;

    private static string Error(string yaml) =>
        Assert.Throws<ConfigException>(() => SiteConfigLoader.Load(yaml, "site.yaml")).Message;

    public static TheoryData<string, string> InvalidConfigs => new()
    {
        // ── 寫了 key 卻沒給值:YamlDotNet 給 null,2.x 在載入或使用時 NullReferenceException(未處理例外)──
        { "config_version: 1\nsite:\nurl_pattern:\n  lang_map: { ch: zh-Hant }\n  default_lang: zh-Hant\nextract:\n  content: main\n",
          "site has no value." },
        { "config_version: 1\nsite:\n  base_url: https://www.example.org\nurl_pattern:\nextract:\n  content: main\n",
          "url_pattern has no value." },
        { "config_version: 1\nsite:\n  base_url: https://www.example.org\nurl_pattern:\n  lang_map: { ch: zh-Hant }\n  default_lang: zh-Hant\nextract:\n",
          "extract has no value." },
        { Yaml(tail: "pairing:\n"), "pairing has no value." },
        { Yaml(tail: "media:\n"), "media has no value." },
        { Yaml(site: "  polite:\n"), "site.polite has no value." },
        { Yaml(site: "  user_agent:\n"), "site.user_agent has no value." },
        { Yaml(site: "  auth_workaround:\n    type: cookie\n    set:\n"),
          "site.auth_workaround.set has no value (write {} for an empty map, or remove the key for the default)." },
        { Yaml(urlPattern: "  strip_extensions:\n"),
          "url_pattern.strip_extensions has no value (write [] for an empty list, or remove the key for the default)." },
        { Yaml(extract: "  title_noise:\n"),
          "extract.title_noise has no value (write [] for an empty list, or remove the key for the default)." },
        { Yaml(extract: "  title_noise: [~]\n"), "extract.title_noise has an item with no value." },
        { Yaml(extract: "  title_markers:\n"),
          "extract.title_markers has no value (write [] for an empty list, or remove the key for the default)." },
        { Yaml(extract: "  strip_title_prefix: [~]\n"), "extract.strip_title_prefix has an item with no value." },
        { Yaml(extract: "  section_types:\n"),
          "extract.section_types has no value (write {} for an empty map, or remove the key for the default)." },
        { Yaml(extract: "  section_flags:\n"),
          "extract.section_flags has no value (write {} for an empty map, or remove the key for the default)." },
        { Yaml(extract: "  type_rules:\n"),
          "extract.type_rules has no value (write {} for an empty map, or remove the key for the default)." },
        { Yaml(extract: "  type_rules:\n    article:\n      remove_selectors:\n"),
          "extract.type_rules.article.remove_selectors has no value (write [] for an empty list, or remove the key for the default)." },
        { Yaml(extract: "  type_rules:\n    article:\n      remove_selectors: [~]\n"),
          "extract.type_rules.article.remove_selectors has an item with no value." },
        { Yaml(tail: "pairing:\n  fallback:\n"),
          "pairing.fallback has no value (write [] for an empty list, or remove the key for the default)." },
        { Yaml(tail: "media:\n  web_prefix:\n"), "media.web_prefix has no value." },
        { Yaml(tail: "media:\n  pdf_labels:\n"),
          "media.pdf_labels has no value (write {} for an empty map, or remove the key for the default)." },

        // ── regex:2.x 到 extract 建 TitleCleaner 時才 ArgumentException;thumbs / probe-orphans 永遠不報 ──
        { Yaml(extract: "  strip_title_prefix: [\"^(Re: \"]\n"),
          "extract.strip_title_prefix: '^(Re: ' is not a valid regular expression (InsufficientClosingParentheses at offset 6)." },

        // ── URL ──
        { Yaml(site: "").Replace("https://www.example.org", "https://www.example.org/?lang=ch"),
          "site.base_url must not have a query or fragment (URLs are built as <base_url>/<path>), got 'https://www.example.org/?lang=ch'." },
        { Yaml(site: "").Replace("https://www.example.org", "https://www.example.org/#top"),
          "site.base_url must not have a query or fragment (URLs are built as <base_url>/<path>), got 'https://www.example.org/#top'." },

        // ── HTTP 標頭:換行會讓請求送不出去,probe 會把它算成「連線錯誤」,一路錯到 exit 2「沒拿到回答」──
        { Yaml(site: "  user_agent: \"Bot/1.0\\r\\nX-Evil: 1\"\n"), "site.user_agent must contain only printable ASCII (HTTP headers can't carry line breaks, control characters or non-ASCII text)." },
        // 非 ASCII:載入時 2.x / 606e01b 都放行,但 .NET 送請求時拒絕非 ASCII 的標頭值——每一個請求都送不出去,
        // probe 全部算成連線錯誤(審查建議 3)
        { Yaml(site: "  user_agent: \"MigrationBot/1.0 (café)\"\n"), "site.user_agent must contain only printable ASCII (HTTP headers can't carry line breaks, control characters or non-ASCII text)." },
        { Yaml(site: "  auth_workaround:\n    type: cookie\n    set: { \"a b\": \"1\" }\n"), "site.auth_workaround.set: cookie name 'a b' must be non-empty printable ASCII without '=', ';' or whitespace." },
        { Yaml(site: "  auth_workaround:\n    type: cookie\n    set: { \"ü\": \"1\" }\n"), "site.auth_workaround.set: cookie name 'ü' must be non-empty printable ASCII without '=', ';' or whitespace." },
        { Yaml(site: "  auth_workaround:\n    type: cookie\n    set: { k: \"1; admin=1\" }\n"), "site.auth_workaround.set.k: cookie value must be printable ASCII without ';' (HTTP headers can't carry control characters or non-ASCII text)." },
        { Yaml(site: "  auth_workaround:\n    type: cookie\n    set: { lang: \"中文\" }\n"), "site.auth_workaround.set.lang: cookie value must be printable ASCII without ';' (HTTP headers can't carry control characters or non-ASCII text)." },

        // ── 數值範圍:409 後的重試等兩倍,超過 int 一半時 Task.Delay 在探測當下才擲例外 ──
        { Yaml(polite: "    delay_ms: 2000000000\n"),
          "site.polite.delay_ms must be <= 1073741823 (a retry after HTTP 409 waits twice as long), got 2000000000." },

        // ── 路徑形狀 ──
        { Yaml().Replace("lang_map: { ch: zh-Hant, en: en }", "lang_map: { \"ch/tw\": zh-Hant, en: en }"),
          "url_pattern.lang_map key 'ch/tw' must be \"\" or a single URL path segment (no '/', '\\' or ':', not '.' or '..')." },
        { Yaml().Replace("lang_map: { ch: zh-Hant, en: en }", "lang_map: { \"..\": zh-Hant, en: en }"),
          "url_pattern.lang_map key '..' must be \"\" or a single URL path segment (no '/', '\\' or ':', not '.' or '..')." },
        { Yaml(urlPattern: "  strip_extensions: [php]\n"),
          "url_pattern.strip_extensions entry 'php' must be a file extension starting with '.' (e.g. .php), without '/', '\\', '?', '#' or whitespace." },
        { Yaml(urlPattern: "  strip_extensions: [\"\", .php]\n"),
          "url_pattern.strip_extensions entry '' must be a file extension starting with '.' (e.g. .php), without '/', '\\', '?', '#' or whitespace." },
        { Yaml(extract: "  section_types: { \"news/2024\": article }\n"),
          "extract.section_types key 'news/2024' must be \"\" (root-level pages) or a single directory name (no '/', '\\' or ':', not '.' or '..')." },
        { Yaml(extract: "  section_flags: { \"..\": needs_rebuild }\n"),
          "extract.section_flags key '..' must be \"\" (root-level pages) or a single directory name (no '/', '\\' or ':', not '.' or '..')." },
        { Yaml(extract: "  section_flags: { \"a:b\": needs_rebuild }\n"),
          "extract.section_flags key 'a:b' must be \"\" (root-level pages) or a single directory name (no '/', '\\' or ':', not '.' or '..')." },
        { Yaml(tail: "media:\n  web_prefix: media/\n"),
          "media.web_prefix must be a root-relative path starting with '/' (e.g. /media/) or an absolute http(s) URL, without whitespace, quotes, '<', '>', '?' or '#', got 'media/'." },

        // ── 列舉值與格式 ──
        { Yaml().Replace("lang_map: { ch: zh-Hant, en: en }", "lang_map: { ch: zh_Hant, en: en }").Replace("default_lang: zh-Hant", "default_lang: en"),
          "url_pattern.lang_map value 'zh_Hant' (for 'ch') is not a well-formed BCP-47 language tag (e.g. en, zh-Hant, pt-BR)." },
        { Yaml(extract: "  section_types: { news: ~ }\n"), "extract.section_types.news has no page type." },
        { Yaml(extract: "  section_flags: { support: \"needs_rebuild;urgent\" }\n"),
          "extract.section_flags.support: flag 'needs_rebuild;urgent' must be non-empty and contain no ';' (content_inventory.csv separates flags with ';')." },
        { Yaml(tail: "pairing:\n  fallback: [date, hreflang, date]\n"), "pairing.fallback lists 'date' more than once." },
        { Yaml(tail: "media:\n  pdf_labels: { zh_Hant: 下載 PDF }\n"),
          "media.pdf_labels key 'zh_Hant' is not a well-formed BCP-47 language tag (e.g. en, zh-Hant, pt-BR)." },
        { Yaml(tail: "media:\n  pdf_labels: { en: \"\" }\n"), "media.pdf_labels.en is empty." },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public void InvalidField_FailsAtLoad_NamingTheField(string yaml, string error)
    {
        Assert.Equal("site.yaml: config is invalid:\n  - " + error, Error(yaml));
    }

    // ── YAML 重複 key:YamlDotNet 預設安靜接受、後者覆蓋前者(審查建議 4)──────────────────────────
    // section_types: { news: article, news: event } 載入後只剩 event,沒有任何提示

    [Fact]
    public void DuplicateKey_InAMap_FailsAtLoad_NamingTheFieldAndLine()
    {
        var yaml = Yaml(extract: "  section_types: { news: article, news: event }\n");   // 第 9 行

        Assert.Equal(
            "site.yaml: config is invalid:\n"
            + "  - extract.section_types: 'news' appears more than once (line 9); the later value would silently replace the earlier one.",
            Error(yaml));
    }

    [Fact]
    public void DuplicateTopLevelSection_FailsAtLoad()
    {
        var yaml = Yaml(tail: "extract:\n  content: \"body\"\n");   // 第二個 extract 在第 9 行

        Assert.Contains("  - 'extract' appears more than once (line 9); the later value would silently replace the earlier one.", Error(yaml));
    }

    [Fact]
    public void DuplicateKey_IsListedTogetherWithTheOtherProblems()
    {
        var yaml = Yaml(
            urlPattern: "  strip_extensions: [php]\n",
            extract: "  type_rules:\n    article:\n      title_selector: \"h3\"\n      title_selector: \"h3[[\"\n");

        var message = Error(yaml);

        Assert.Contains("  - extract.type_rules.article: 'title_selector' appears more than once (line 13);", message);
        Assert.Contains("  - url_pattern.strip_extensions entry 'php'", message);
        Assert.Contains("  - extract.type_rules.article.title_selector: 'h3[['", message);   // 後者照樣驗
    }

    [Fact]
    public void SameKeyInDifferentMaps_IsNotADuplicate()
    {
        var config = SiteConfigLoader.Load(Yaml(extract:
            "  section_types: { news: article }\n  section_flags: { news: needs_rebuild }\n"
            + "  type_rules:\n    article:\n      remove_selectors: [h5, h5]\n    event:\n      remove_selectors: [h5]\n"));

        Assert.Equal("needs_rebuild", config.Extract.SectionFlags["news"]);
    }

    [Fact]
    public void EveryProblem_IsListedAtOnce()
    {
        var yaml = Yaml(
            urlPattern: "  strip_extensions: [php]\n",
            extract: "  strip_title_prefix: [\"(\"]\n  section_flags: { support: \"a;b\" }\n",
            tail: "media:\n  web_prefix: media/\n");

        var message = Error(yaml);

        Assert.Equal(5, message.Split('\n').Length);   // 標題一行 + 四個錯誤
        Assert.Contains("url_pattern.strip_extensions entry 'php'", message);
        Assert.Contains("extract.strip_title_prefix: '('", message);
        Assert.Contains("extract.section_flags.support: flag 'a;b'", message);
        Assert.Contains("media.web_prefix must be", message);
    }

    [Fact]
    public void ValidEdgeCases_StillLoad()
    {
        // 收緊的是「寫壞」,不是合法但少見的寫法
        var yaml = "config_version: 1\n"
            + "site:\n  base_url: https://www.example.org/legacy/\n  user_agent: \"\"\n"
            + "  auth_workaround:\n    type: cookie\n    set: { humans_21909: \"1\", token: \"YWJj==\", note: \"two words\" }\n"
            + "url_pattern:\n  lang_map: { \"\": zh-Hant-TW, en: en, x: x-klingon }\n  default_lang: zh-Hant-TW\n  strip_extensions: [.php, .aspx, .php5]\n"
            + "extract:\n  content: main\n  title_noise: [\"\"]\n  title_markers: []\n  strip_title_prefix: [\"^\\\\[(?:公告|News)\\\\]\\\\s*\"]\n"
            + "  section_types: { news: article, \"zh.tw\": page }\n  section_flags: { support: needs_rebuild }\n"
            + "  type_rules:\n    article:\n    event:\n      remove_selectors: []\n"
            + "pairing:\n  fallback: []\n"
            + "media:\n  web_prefix: https://cdn.example.org/media\n  pdf_labels: { zh-Hant-TW: 下載 PDF }\n";

        var config = SiteConfigLoader.Load(yaml, "site.yaml");

        Assert.Equal("https://cdn.example.org/media", config.Media.WebPrefix);
        Assert.Null(config.Extract.TypeRules["article"]);   // 頁型寫了但沒規則 = 沒有規則(用到時本來就這樣處理)
    }

    [Fact]
    public void RootLevelSectionKey_IsAllowed_AndAppliesToRootLevelPages()
    {
        // 根層頁(raw/about.php.html、無語言前綴站的 raw/x.php.html)的 section 就是 ""。2.3.1 會套用這個 key;
        // 606e01b 把它當成「不是單一目錄名」擋掉了(審查必修 2)。比照 lang_map 允許 "",其餘形狀照擋
        var config = SiteConfigLoader.Load(Yaml(extract: "  section_types: { \"\": article }\n  section_flags: { \"\": needs_rebuild }\n"));

        Assert.Equal("article", new PolyMigrate.Core.Extraction.PageClassifier(config.Extract).Classify("", "about"));
        Assert.Equal("needs_rebuild", config.Extract.SectionFlags[""]);
    }

    [Fact]
    public void PdfLabelForALocaleOutsideLangMap_LoadsWithAWarning()
    {
        // 共用模板常帶好幾種語言的標籤(zh-Hant、ja…),這個站的 lang_map 只有其中幾種:用不到,但不是寫壞——
        // 606e01b 把它當成錯誤擋掉。形狀合法的 BCP-47 放行,載入時警告點名(大小寫不同的 zh-hant 也一樣:查表是精確比對,用不到)
        var warnings = new List<string>();

        var config = SiteConfigLoader.Load(
            Yaml(tail: "media:\n  pdf_labels: { zh-Hant: 下載 PDF, ja: PDF を表示, zh-hant: 下載 }\n"), "site.yaml", warnings);

        Assert.Equal(3, config.Media.PdfLabels.Count);
        Assert.Equal(
        [
            "media.pdf_labels key 'ja' is not one of the lang_map locales (zh-Hant, en); that label is never used.",
            "media.pdf_labels key 'zh-hant' is not one of the lang_map locales (zh-Hant, en); that label is never used.",
        ], warnings);
    }

    [Fact]
    public void ValidConfig_HasNoWarnings()
    {
        var warnings = new List<string>();

        SiteConfigLoader.Load(Yaml(tail: "media:\n  pdf_labels: { zh-Hant: 下載 PDF, en: View PDF }\n"), "site.yaml", warnings);

        Assert.Empty(warnings);
    }

    [Fact]
    public void RootRelativeWebPrefix_AndEmptyStripExtensions_StillLoad()
    {
        var config = SiteConfigLoader.Load(Yaml(urlPattern: "  strip_extensions: []\n", tail: "media:\n  web_prefix: /assets\n"));

        Assert.Empty(config.UrlPattern.StripExtensions);
        Assert.Equal("/assets", config.Media.WebPrefix);
    }
}
