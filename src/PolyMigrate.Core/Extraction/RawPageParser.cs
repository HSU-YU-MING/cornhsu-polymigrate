using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 一個鏡像 HTML 檔的身分(規格 rel_from_raw)。
/// </summary>
/// <param name="FilePath">鏡像檔絕對路徑。</param>
/// <param name="LangPrefix">URL 語言前綴(如 "ch");無前綴頁為 ""。</param>
/// <param name="Locale">BCP-47 locale(§3.3);無前綴頁歸 default_lang。</param>
/// <param name="Section">語言前綴後的第一層目錄;頁面直接在語言根目錄下則為 ""。</param>
/// <param name="Slug">檔名去尾綴;空檔名為 "index"。</param>
/// <param name="SourceUrl">還原的原始網址(含動態副檔名,如 .php)。</param>
/// <param name="TranslationKey">去語言前綴的路徑,雙語配對鍵(§1.4)。
/// 無語言前綴的站級頁(如語言選擇頁)以 "/" 開頭,不與任何語言版配對——
/// 它們不是誰的翻譯,不可與同名的去前綴路徑相撞。</param>
public sealed record RawPage(
    string FilePath,
    string LangPrefix,
    string Locale,
    string Section,
    string Slug,
    string SourceUrl,
    string TranslationKey);

/// <summary>鏡像檔路徑 → 頁面身分。鏡像檔名規則:原始 URL 路徑 + ".html"(如 ch/news/a.php → ch/news/a.php.html)。</summary>
public sealed class RawPageParser
{
    private readonly UrlPatternSection _urlPattern;
    private readonly string _baseUrl;
    private readonly string[] _suffixes;   // 長的先比:".php.html" 再 ".html"

    public RawPageParser(SiteSection site, UrlPatternSection urlPattern)
    {
        _urlPattern = urlPattern;
        _baseUrl = site.BaseUrl.TrimEnd('/');
        _suffixes = [.. urlPattern.StripExtensions.Select(e => e + ".html"), ".html"];
    }

    public RawPage Parse(string rawRoot, string filePath)
    {
        var rel = Path.GetRelativePath(rawRoot, filePath).Replace('\\', '/');
        var parts = rel.Split('/');

        var langPrefix = _urlPattern.LangMap.ContainsKey(parts[0]) ? parts[0] : "";
        var locale = langPrefix.Length > 0
            ? _urlPattern.LangMap[langPrefix]
            : _urlPattern.DefaultLang;

        // 語言前綴之後的路徑;規格假設前綴必在,無前綴時整條路徑照用(不像 Python 一律丟 parts[0])
        var tail = langPrefix.Length > 0 ? parts[1..] : parts;
        var section = tail.Length > 1 ? tail[0] : "";

        var slug = StripSuffix(tail[^1]);
        if (slug.Length == 0)
        {
            slug = "index";
        }

        var urlPath = rel.EndsWith(".html", StringComparison.Ordinal) ? rel[..^".html".Length] : rel;
        var sourceUrl = $"{_baseUrl}/{urlPath}";

        var translationKey = StripSuffix(string.Join('/', tail));
        if (langPrefix.Length == 0)
        {
            translationKey = "/" + translationKey;
        }

        return new RawPage(filePath, langPrefix, locale, section, slug, sourceUrl, translationKey);
    }

    private string StripSuffix(string name)
    {
        foreach (var suffix in _suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }
        return name;
    }
}
