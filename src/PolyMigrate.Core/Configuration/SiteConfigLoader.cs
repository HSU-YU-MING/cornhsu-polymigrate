using System.Text.RegularExpressions;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Pairing;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PolyMigrate.Core.Configuration;

/// <summary>config 載入或驗證失敗(§3.9)。訊息面向使用者,CLI 直接印出。</summary>
public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// YAML site config 載入器(§3.9):underscored 命名、未知欄位報錯(不默默忽略)、載入後驗證。
/// <para>3.0 起「寫壞的 config 不該等到某個指令才炸」:每個欄位逐一看過,寫壞了會到使用時才擲例外、或安靜地產出錯東西的
/// (regex、URL、路徑形狀、列舉值、數值範圍、重複的 key、YAML 寫了 key 卻沒給值),都在這裡驗,一次列出全部、點名欄位。
/// 不是每一種錯都驗得出來(selector 在真實頁面上選不選得到東西、頁型有沒有拼錯…),沒驗的列在 docs/contracts.md。用到時的檢查保留為第二道防線
/// (函式庫呼叫端可以不經 loader、直接在程式裡組 <see cref="SiteConfig"/>)。</para>
/// </summary>
public static class SiteConfigLoader
{
    /// <summary>
    /// <c>site.polite.delay_ms</c> 的上限:probe 遇到 409 的重試等兩倍(<c>DelayMs * 2</c>),
    /// 超過 int 一半時乘出負數,<c>Task.Delay</c> 要到探測當下才擲例外。
    /// </summary>
    internal const int MaxDelayMs = int.MaxValue / 2;

    /// <summary>BCP-47 的語法形狀(RFC 5646:主語言 2～8 字母或 x / i 開頭的私用與舊式標記,之後每段 1～8 個英數字)。</summary>
    private static readonly Regex Bcp47 = new(@"^(?:[A-Za-z]{2,8}|[xXiI])(?:-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant);

    public static SiteConfig LoadFile(string path) => LoadFile(path, []);

    /// <summary>
    /// 同上,另把載入時的 warning(用不到、但不是寫壞的設定,例如 lang_map 裡沒有的 pdf_labels)收進
    /// <paramref name="warnings"/>。CLI 用這個,照既有慣例印在 stderr、不影響 exit code。
    /// </summary>
    internal static SiteConfig LoadFile(string path, List<string> warnings)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Config file not found: {path}");
        }
        return Load(File.ReadAllText(path), path, warnings);
    }

    public static SiteConfig Load(string yaml, string sourceName = "<string>") => Load(yaml, sourceName, []);

    /// <summary>同上,另收集載入時的 warning(見 <see cref="LoadFile(string, List{string})"/>)。</summary>
    internal static SiteConfig Load(string yaml, string sourceName, List<string> warnings)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();   // 不設 IgnoreUnmatchedProperties → 未知欄位一律報錯

        SiteConfig config;
        List<string> duplicates;
        try
        {
            duplicates = DuplicateKeys(yaml);
            config = deserializer.Deserialize<SiteConfig>(yaml)
                ?? throw new ConfigException($"{sourceName}: config is empty.");
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"{sourceName}: invalid config — {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        Validate(config, sourceName, duplicates, warnings);
        return config;
    }

    /// <summary>
    /// 同一個對照表裡重複的 key。YamlDotNet 預設安靜接受、後者覆蓋前者(<c>section_types: { news: article, news: event }</c>
    /// 載入後只剩 event,沒有任何提示;審查建議 4)。逐一走解析事件,記下每個對照表的路徑,訊息點名欄位與行號;
    /// 與其餘的驗證錯誤一起列出(後者照樣驗——用的是覆蓋後的值)。
    /// </summary>
    private static List<string> DuplicateKeys(string yaml)
    {
        var problems = new List<string>();
        var stack = new Stack<YamlLevel>();
        var parser = new Parser(new StringReader(yaml));
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart or SequenceStart:
                    var path = stack.TryPeek(out var parent) ? parent.ChildPath() : "";
                    stack.Push(new YamlLevel(parser.Current is MappingStart, path));
                    break;
                case MappingEnd or SequenceEnd:
                    stack.Pop();
                    break;
                case Scalar scalar when stack.TryPeek(out var level):
                    if (level.Mapping && level.ExpectKey && !level.Keys.Add(scalar.Value))
                    {
                        var where = level.Path.Length == 0 ? "" : $"{level.Path}: ";
                        problems.Add($"{where}'{scalar.Value}' appears more than once (line {scalar.Start.Line}); the later value would silently replace the earlier one.");
                    }
                    level.Consume(scalar.Value);
                    break;
                case AnchorAlias when stack.TryPeek(out var level):
                    level.Consume(null);
                    break;
            }
        }
        return problems;
    }

    /// <summary><see cref="DuplicateKeys"/> 走到的一層對照表或清單。</summary>
    private sealed class YamlLevel(bool mapping, string path)
    {
        public bool Mapping { get; } = mapping;
        public string Path { get; } = path;
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
        public bool ExpectKey { get; private set; } = true;
        private string? _key;
        private int _index;

        /// <summary>下一個子節點(對照表的值、清單的項目)的路徑,並把這一層推進到下一格。</summary>
        public string ChildPath()
        {
            if (!Mapping)
            {
                return $"{Path}[{_index++}]";
            }
            if (ExpectKey)
            {
                ExpectKey = false;   // 複合 key(很少見):不追蹤它的路徑
                return $"{Path}.?";
            }
            ExpectKey = true;
            return Path.Length == 0 ? _key ?? "" : $"{Path}.{_key}";
        }

        /// <summary>這一層讀到一個純量(對照表的 key 或值、清單的項目)。</summary>
        public void Consume(string? scalar)
        {
            if (!Mapping)
            {
                _index++;
            }
            else if (ExpectKey)
            {
                _key = scalar;
                ExpectKey = false;
            }
            else
            {
                ExpectKey = true;
            }
        }
    }

    private static void Validate(SiteConfig c, string src, List<string> duplicates, List<string> warnings)
    {
        var errors = new List<string>(duplicates);

        if (c.ConfigVersion != 1)
        {
            errors.Add($"config_version {c.ConfigVersion} is not supported (expected 1).");
        }
        // YAML 寫了 key 卻沒給值(「site:」後面什麼都沒有)→ YamlDotNet 給 null。2.x 不擋:在這裡或用到時
        // NullReferenceException,是未處理例外、exit code 不在契約內。區段是 null 就只報這一條,不往下驗它的欄位
        if (Present(errors, c.Site, "site") is { } site)
        {
            ValidateSite(errors, site);
        }
        if (Present(errors, c.UrlPattern, "url_pattern") is { } urlPattern)
        {
            ValidateUrlPattern(errors, urlPattern);
        }
        if (Present(errors, c.Extract, "extract") is { } extract)
        {
            ValidateExtract(errors, extract);
        }
        if (Present(errors, c.Pairing, "pairing") is { } pairing && List(errors, pairing.Fallback, "pairing.fallback") is { } fallback)
        {
            foreach (var h in fallback.Where(h => !PairingSuggester.KnownHeuristics.Contains(h)))
            {
                errors.Add($"pairing.fallback '{h}' is not a known heuristic ({string.Join(", ", PairingSuggester.KnownHeuristics)}).");
            }
            // 重複列出會讓同一種證據加兩次權重、pair_evidence 也寫兩次(2.x 安靜地照做)
            foreach (var h in fallback.Where(h => h is not null).GroupBy(h => h, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                errors.Add($"pairing.fallback lists '{h.Key}' more than once.");
            }
        }
        if (Present(errors, c.Media, "media") is { } media)
        {
            ValidateMedia(errors, warnings, media, c.UrlPattern?.LangMap);
        }

        if (errors.Count > 0)
        {
            throw new ConfigException($"{src}: " + string.Join("\n  - ", ["config is invalid:", .. errors]));
        }
    }

    private static void ValidateSite(List<string> errors, SiteSection site)
    {
        if (!Uri.TryCreate(site.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add($"site.base_url must be an absolute http(s) URL, got '{site.BaseUrl}'.");
        }
        else if (baseUri.Query.Length > 0 || baseUri.Fragment.Length > 0)
        {
            // 網址一律組成 <base_url>/<路徑>:帶 query 或 fragment 時,路徑會接在 ?… 或 #… 後面,2.x 照樣發出去
            errors.Add($"site.base_url must not have a query or fragment (URLs are built as <base_url>/<path>), got '{site.BaseUrl}'.");
        }
        if (site.Encoding is { } enc)
        {
            try
            {
                _ = TextEncodings.Resolve(enc);
            }
            catch (ArgumentException)
            {
                errors.Add($"site.encoding '{enc}' is not a known encoding.");
            }
        }
        if (Present(errors, site.Polite, "site.polite") is { } polite)
        {
            if (polite.DelayMs < 0)
            {
                errors.Add($"site.polite.delay_ms must be >= 0, got {polite.DelayMs}.");
            }
            else if (polite.DelayMs > MaxDelayMs)
            {
                errors.Add($"site.polite.delay_ms must be <= {MaxDelayMs} (a retry after HTTP 409 waits twice as long), got {polite.DelayMs}.");
            }
        }
        // User-Agent 與 Cookie 以 TryAddWithoutValidation 放進標頭:換行、控制字元與非 ASCII(.NET 送請求時拒絕
        // 非 ASCII 的標頭值)都會讓每一個請求送不出去,probe 把它算成「連線錯誤」,一路錯到「一個回答都沒拿到」。
        // 只收可列印的 ASCII(0x20～0x7E;審查建議 3)
        if (Present(errors, site.UserAgent, "site.user_agent") is { } userAgent && !userAgent.All(IsPrintableAscii))
        {
            errors.Add("site.user_agent must contain only printable ASCII (HTTP headers can't carry line breaks, control characters or non-ASCII text).");
        }
        if (site.AuthWorkaround is { } auth)
        {
            if (auth.Type != "cookie")
            {
                errors.Add($"site.auth_workaround.type '{auth.Type}' is not supported (only 'cookie').");
            }
            if (Map(errors, auth.Set, "site.auth_workaround.set") is { } cookies)
            {
                foreach (var (name, value) in cookies)
                {
                    if (name.Length == 0 || !name.All(ch => IsPrintableAscii(ch) && ch is not ('=' or ';' or ' ')))
                    {
                        errors.Add($"site.auth_workaround.set: cookie name '{name}' must be non-empty printable ASCII without '=', ';' or whitespace.");
                    }
                    if (value is not null && !value.All(ch => IsPrintableAscii(ch) && ch != ';'))
                    {
                        errors.Add($"site.auth_workaround.set.{name}: cookie value must be printable ASCII without ';' (HTTP headers can't carry control characters or non-ASCII text).");
                    }
                }
            }
        }
    }

    private static void ValidateUrlPattern(List<string> errors, UrlPatternSection urlPattern)
    {
        if (Map(errors, urlPattern.LangMap, "url_pattern.lang_map") is { } langMap)
        {
            if (langMap.Count == 0)
            {
                errors.Add("url_pattern.lang_map must map at least one URL prefix to a BCP-47 locale (single-language sites use one entry).");
            }
            else if (langMap.Any(kv => string.IsNullOrWhiteSpace(kv.Value)))
            {
                errors.Add("url_pattern.lang_map values (BCP-47 locales) must be non-empty.");
            }
            foreach (var (prefix, locale) in langMap)
            {
                // URL 的第一段、也是 raw/ 底下的目錄名:含斜線的 key 永遠對不上任何鏡像頁,'..' 會讓 probe 讀到 raw/ 以外
                if (prefix.Length > 0 && !IsPathSegment(prefix))
                {
                    errors.Add($"url_pattern.lang_map key '{prefix}' must be \"\" or a single URL path segment (no '/', '\\' or ':', not '.' or '..').");
                }
                if (!string.IsNullOrWhiteSpace(locale) && !Bcp47.IsMatch(locale))
                {
                    errors.Add($"url_pattern.lang_map value '{locale}' (for '{prefix}') is not a well-formed BCP-47 language tag (e.g. en, zh-Hant, pt-BR).");
                }
            }
        }
        if (string.IsNullOrWhiteSpace(urlPattern.DefaultLang))
        {
            errors.Add("url_pattern.default_lang is required.");
        }
        else if (urlPattern.LangMap is { Count: > 0 } map && !map.ContainsValue(urlPattern.DefaultLang))
        {
            errors.Add($"url_pattern.default_lang '{urlPattern.DefaultLang}' must be one of the lang_map locales ({string.Join(", ", map.Values)}).");
        }
        if (List(errors, urlPattern.StripExtensions, "url_pattern.strip_extensions") is { } extensions)
        {
            // 用法是「路徑以它結尾就剝掉」與 probe / fetch 網址的尾綴:沒有點的會剝到 slug 中間("php" → "/a."),
            // 空字串永遠成立、讓後面的副檔名全部失效(["", ".php"] 等於不剝 .php)
            foreach (var extension in extensions.Where(e => e is not null))
            {
                if (extension.Length < 2 || extension[0] != '.'
                    || extension.Any(ch => ch is '/' or '\\' or '?' or '#' || char.IsWhiteSpace(ch) || char.IsControl(ch)))
                {
                    errors.Add($"url_pattern.strip_extensions entry '{extension}' must be a file extension starting with '.' (e.g. .php), without '/', '\\', '?', '#' or whitespace.");
                }
            }
        }
    }

    private static void ValidateExtract(List<string> errors, ExtractSection extract)
    {
        if (string.IsNullOrWhiteSpace(extract.Content))
        {
            errors.Add("extract.content (CSS selector for the main content) is required.");
        }
        else
        {
            AddSelectorProblem(errors, extract.Content, "extract.content");
        }
        // 空字串刻意允許(用到時略過);null 項目在用到時才擲 ArgumentNullException
        List(errors, extract.TitleNoise, "extract.title_noise");
        List(errors, extract.TitleMarkers, "extract.title_markers");
        // regex 與用到時(TitleCleaner)同一組選項編譯:2.x 要到 extract 建標題清理器時才 exit 2,
        // 訊息是 .NET 的「Invalid pattern …」、沒點名欄位;thumbs / probe-orphans 則永遠不報
        if (List(errors, extract.StripTitlePrefix, "extract.strip_title_prefix") is { } prefixes)
        {
            foreach (var pattern in prefixes.Where(p => p is not null))
            {
                try
                {
                    _ = new Regex(pattern, TitleCleaner.PrefixOptions);
                }
                catch (RegexParseException ex)
                {
                    errors.Add($"extract.strip_title_prefix: '{pattern}' is not a valid regular expression ({ex.Error} at offset {ex.Offset}).");
                }
            }
        }
        // section 名稱是 raw/ 底下的目錄名:含斜線的 key 永遠對不上任何頁(2.x 安靜地當成沒設定)。
        // "" 合法:根層頁(raw/about.php.html)的 section 就是 "",2.3.1 會套用它(審查必修 2)
        if (Map(errors, extract.SectionTypes, "extract.section_types") is { } sectionTypes)
        {
            foreach (var (section, pageType) in sectionTypes)
            {
                if (!IsSectionKey(section))
                {
                    errors.Add($"extract.section_types key '{section}' must be {SectionKeyRule}.");
                }
                if (string.IsNullOrWhiteSpace(pageType))
                {
                    // null 頁型要到用到 type_rules 時才擲 ArgumentNullException
                    errors.Add($"extract.section_types.{section} has no page type.");
                }
            }
        }
        if (Map(errors, extract.SectionFlags, "extract.section_flags") is { } sectionFlags)
        {
            foreach (var (section, flag) in sectionFlags)
            {
                if (!IsSectionKey(section))
                {
                    errors.Add($"extract.section_flags key '{section}' must be {SectionKeyRule}.");
                }
                if (string.IsNullOrWhiteSpace(flag) || flag.Contains(';'))
                {
                    errors.Add($"extract.section_flags.{section}: flag '{flag}' must be non-empty and contain no ';' (content_inventory.csv separates flags with ';').");
                }
            }
        }
        // type_rules 的 selector 全部驗,不管這次有沒有頁屬於那個頁型、也不管是哪個指令在載入(3.0 起)。
        // 頁型寫了但沒給規則(「article:」後面什麼都沒有)= 沒有規則,用到時本來就這樣處理,不算錯
        if (Map(errors, extract.TypeRules, "extract.type_rules") is { } typeRules)
        {
            foreach (var (type, rules) in typeRules)
            {
                if (rules is null)
                {
                    continue;
                }
                if (rules.TitleSelector is { } titleSelector)
                {
                    AddSelectorProblem(errors, titleSelector, $"extract.type_rules.{type}.title_selector");
                }
                foreach (var selector in List(errors, rules.RemoveSelectors, $"extract.type_rules.{type}.remove_selectors") ?? [])
                {
                    AddSelectorProblem(errors, selector, $"extract.type_rules.{type}.remove_selectors");
                }
            }
        }
        if (extract.TextInImageMaxLength < 0)
        {
            errors.Add($"extract.text_in_image_max_length must be >= 0, got {extract.TextInImageMaxLength}.");
        }
    }

    private static void ValidateMedia(List<string> errors, List<string> warnings, MediaSection media, Dictionary<string, string>? langMap)
    {
        // 寫進 Markdown 的媒體網址前綴:相對路徑在巢狀頁上會指錯地方,引號、角括號會弄壞內嵌 HTML
        if (Present(errors, media.WebPrefix, "media.web_prefix") is { } webPrefix && !IsWebPrefix(webPrefix))
        {
            errors.Add($"media.web_prefix must be a root-relative path starting with '/' (e.g. /media/) or an absolute http(s) URL, without whitespace, quotes, '<', '>', '?' or '#', got '{media.WebPrefix}'.");
        }
        // 依頁面的 locale 精確查表:key 不在 lang_map 裡(或大小寫不同)的那一條永遠用不到,2.x 安靜地退回 "View PDF"。
        // 形狀不對的 key 是寫壞(error);形狀合法、只是這個站沒有那個語言的,多半是共用模板帶來的(zh-Hant、ja…),
        // 只警告點名,不擋(審查可接受項;606e01b 一度當成錯誤)
        if (Map(errors, media.PdfLabels, "media.pdf_labels") is { } labels)
        {
            var locales = langMap?.Values.Where(v => v is not null).Distinct().ToList() ?? [];
            foreach (var (locale, label) in labels)
            {
                if (!Bcp47.IsMatch(locale))
                {
                    errors.Add($"media.pdf_labels key '{locale}' is not a well-formed BCP-47 language tag (e.g. en, zh-Hant, pt-BR).");
                }
                else if (locales.Count > 0 && !locales.Contains(locale, StringComparer.Ordinal))
                {
                    warnings.Add($"media.pdf_labels key '{locale}' is not one of the lang_map locales ({string.Join(", ", locales)}); that label is never used.");
                }
                if (string.IsNullOrEmpty(label))
                {
                    errors.Add($"media.pdf_labels.{locale} is empty.");
                }
            }
        }
        if (media.Thumbnails is { } thumbs)
        {
            if (thumbs.MaxWidth < 1)
            {
                errors.Add($"media.thumbnails.max_width must be >= 1, got {thumbs.MaxWidth}.");
            }
            if (thumbs.Quality is < 1 or > 100)
            {
                errors.Add($"media.thumbnails.quality must be 1-100, got {thumbs.Quality}.");
            }
        }
    }

    /// <summary>區段或純量寫了 key 卻沒給值 → 報一條、回 null(呼叫端就不再往下驗)。</summary>
    private static T? Present<T>(List<string> errors, T? value, string field)
        where T : class
    {
        if (value is null)
        {
            errors.Add($"{field} has no value.");
        }
        return value;
    }

    /// <summary>清單寫了 key 卻沒給值、或其中有沒給值的項目(<c>[~]</c>、<c>- </c>)時報錯;回傳可用的清單(null 時回 null)。</summary>
    private static List<string>? List(List<string> errors, List<string>? list, string field)
    {
        if (list is null)
        {
            errors.Add($"{field} has no value (write [] for an empty list, or remove the key for the default).");
            return null;
        }
        if (list.Any(item => item is null))
        {
            errors.Add($"{field} has an item with no value.");
        }
        return list;
    }

    private static Dictionary<string, TValue>? Map<TValue>(List<string> errors, Dictionary<string, TValue>? map, string field)
    {
        if (map is null)
        {
            errors.Add($"{field} has no value (write {{}} for an empty map, or remove the key for the default).");
        }
        return map;
    }

    /// <summary>根相對路徑(<c>/media/</c>)或絕對 http(s) 網址,而且沒有會弄壞網址或內嵌 HTML 的字元。</summary>
    private static bool IsWebPrefix(string prefix) =>
        (prefix.StartsWith('/')
         || (Uri.TryCreate(prefix, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        && !prefix.Any(ch => ch is '"' or '\'' or '<' or '>' or '?' or '#' || char.IsWhiteSpace(ch) || char.IsControl(ch));

    /// <summary>可列印的 ASCII(0x20 空白～0x7E <c>~</c>):HTTP 標頭值唯一安全的範圍。</summary>
    private static bool IsPrintableAscii(char ch) => ch is >= ' ' and <= '~';

    private const string SectionKeyRule =
        "\"\" (root-level pages) or a single directory name (no '/', '\\' or ':', not '.' or '..')";

    /// <summary>section_types / section_flags 的 key:"" 或單一路徑段(與 lang_map 的 key 同一條規則)。</summary>
    private static bool IsSectionKey(string value) => value.Length == 0 || IsPathSegment(value);

    /// <summary>單一路徑段:非空、不含 / \ :、不是 . 或 ..(與 slugs 的 --section / --lang 同一條規則)。</summary>
    private static bool IsPathSegment(string value) =>
        value.Length > 0 && value is not ("." or "..") && value.AsSpan().IndexOfAny('/', '\\', ':') < 0;

    private static void AddSelectorProblem(List<string> errors, string? selector, string field)
    {
        if (selector is not null && CssSelectors.Problem(selector, field) is { } problem)
        {
            errors.Add(problem);
        }
    }
}
