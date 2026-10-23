using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 內文連結改寫(規格 rewrite_link):站內動態頁相對連結 → 新路由(以頁面 URL 解析 ../);
/// 外部、錨點、mailto 等保留原樣。
/// </summary>
internal sealed class LinkRewriter
{
    private static readonly string[] PassThroughPrefixes =
        ["http://", "https://", "#", "mailto:", "tel:", "//", "javascript:"];

    private readonly string _host;
    private readonly string[] _stripExtensions;
    private readonly HashSet<string> _langPrefixes;
    private readonly IReadOnlySet<string>? _pageRoutes;

    /// <param name="pageRoutes">
    /// 鏡像裡每一頁的路由(<see cref="RouteForPath"/> 算出來的)。連結目標是鏡像頁時一律照頁的規則改寫;
    /// 只有目標不是鏡像頁,才考慮「它其實是個檔案」(見 <see cref="Rewrite"/>)。null = 不知道鏡像有哪些頁:
    /// 一律照頁的規則,與 9d17071 逐位元相同——日後新的呼叫端忘了傳時,至少不會把指向鏡像頁的連結改壞(最終驗證 I5)。
    /// </param>
    public LinkRewriter(SiteSection site, UrlPatternSection urlPattern, IReadOnlySet<string>? pageRoutes = null)
    {
        _host = new Uri(site.BaseUrl).Host;
        _stripExtensions = [.. urlPattern.StripExtensions];
        _langPrefixes = new HashSet<string>(urlPattern.LangMap.Keys.Where(k => k.Length > 0), StringComparer.Ordinal);
        _pageRoutes = pageRoutes;
    }

    public string Rewrite(string? href, string pageUrl)
    {
        if (string.IsNullOrEmpty(href)
            || PassThroughPrefixes.Any(p => href.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return href ?? "";
        }

        Uri resolved;
        try
        {
            resolved = new Uri(new Uri(pageUrl), href);
        }
        catch (UriFormatException)
        {
            return href;
        }
        if (!string.Equals(resolved.Host, _host, StringComparison.OrdinalIgnoreCase))
        {
            return href;
        }
        var route = RouteForPath(resolved.AbsolutePath);
        // 指向鏡像頁的連結一律照頁的規則(與 9d17071 逐位元相同,第三輪審查的原則);
        // 不是鏡像頁的目標才另外判斷:同 host 的非 http(s)(ftp:// 等)原樣保留,看起來是檔案的不加尾斜線
        if (_pageRoutes is not null && !_pageRoutes.Contains(route))
        {
            if (resolved.Scheme is not ("http" or "https"))
            {
                return href;
            }
            route = FileRoute(resolved.AbsolutePath) ?? route;
        }
        // 保留 query 與 fragment:news.php?id=5 與 #team 不能在改寫時被丟掉
        return route + resolved.Query + resolved.Fragment;
    }

    /// <summary>
    /// 絕對 URL → 站內路由;**不同 host 一律回 null**。
    ///
    /// <para>host 這一關看起來多餘,其實是 hreflang 配對的安全門:很多站的英文版在
    /// <c>en.example.org</c> 而不是 <c>/en/</c>,若只比對路徑,站外的
    /// <c>https://en.example.org/ch/news/a.php</c> 會算出與本站
    /// <c>/ch/news/a</c> 一模一樣的路由,於是一頁被當成自己的翻譯。</para>
    /// </summary>
    public string? RouteForUrl(string absoluteUrl) =>
        Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, _host, StringComparison.OrdinalIgnoreCase)
            ? RouteForPath(uri.AbsolutePath)
            : null;

    /// <summary>
    /// 站內路徑 → 頁的路由:去動態副檔名、收攏 index。
    /// 內文連結改寫與 redirect_map 的 new_path、hreflang 的目標共用同一套規則(兩者不同步 = 301 打到破頁)。
    /// <para><b>這是頁的路由,不做任何「檔案」判斷</b>:redirect_map 的每一列都來自真的頁,在這裡猜「像不像檔案」
    /// 只會誤判(9254591 曾讓 /jane.doe.php、/CONTACT.PHP、/calendar.html 的路由與 301 改變,第三輪審查)。
    /// 必須與 9d17071 逐位元相同,PageRouteStabilityTests 釘住。</para>
    /// </summary>
    public string RouteForPath(string absolutePath)
    {
        var path = absolutePath;
        foreach (var ext in _stripExtensions)
        {
            if (path.EndsWith(ext, StringComparison.Ordinal))
            {
                path = path[..^ext.Length];
                break;
            }
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[^1] == "index")
        {
            parts = parts[..^1];
        }
        return parts.Length switch
        {
            0 => "/",
            // 只剩一段:語言首頁(/ch/index.php → /ch/)或無語言前綴的根層頁(/about.php → /about/),加尾斜線
            1 => $"/{parts[0]}/",
            _ => "/" + string.Join('/', parts),
        };
    }

    /// <summary>
    /// 不是鏡像頁的連結目標,是不是「檔案」:是的話回傳不加尾斜線的路由,否則 null(照頁的規則)。
    /// 只有單段路徑會因此不同(多段本來就不加尾斜線)。下列一律當頁,不做判斷:剝掉了 strip_extensions、
    /// 原本以 / 結尾、或那一段是語言前綴(<c>/index</c> 沒有副檔名,本來就不會被當成檔案)。剩下的才看名字:點檔案(.htaccess),或副檔名是
    /// 1～16 個 ASCII 英數字、至少一個字母(.swf、.pdf、.mp4、.webmanifest)——
    /// <c>v1.2-rc</c> 的 <c>.2-rc</c>、<c>2019.05.01</c> 的 <c>.01</c> 不算。
    /// </summary>
    private string? FileRoute(string absolutePath)
    {
        if (absolutePath.EndsWith('/') || _stripExtensions.Any(e => absolutePath.EndsWith(e, StringComparison.Ordinal)))
        {
            return null;
        }
        var parts = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1 || _langPrefixes.Contains(parts[0]) || !LooksLikeFileName(parts[0]))
        {
            return null;
        }
        return "/" + parts[0];
    }

    private static bool LooksLikeFileName(string segment)
    {
        var dot = segment.LastIndexOf('.');
        if (dot == 0)
        {
            return segment.Length > 1;   // 點檔案:.htaccess
        }
        if (dot < 0)
        {
            return false;
        }
        var extension = segment[(dot + 1)..];
        return extension.Length is >= 1 and <= 16
            && extension.All(char.IsAsciiLetterOrDigit)
            && extension.Any(char.IsAsciiLetter);
    }
}
