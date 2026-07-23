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

    public LinkRewriter(SiteSection site, UrlPatternSection urlPattern)
    {
        _host = new Uri(site.BaseUrl).Host;
        _stripExtensions = [.. urlPattern.StripExtensions];
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
        // 保留 query 與 fragment:news.php?id=5 與 #team 不能在改寫時被丟掉
        return RouteForPath(resolved.AbsolutePath) + resolved.Query + resolved.Fragment;
    }

    /// <summary>
    /// 站內路徑 → 新路由:去動態副檔名、收攏 index。
    /// 內文連結改寫與 redirect_map 的 new_path 共用同一套規則(兩者不同步 = 301 打到破頁)。
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
            1 => $"/{parts[0]}/",   // 只剩語言前綴 → 該語首頁
            _ => "/" + string.Join('/', parts),
        };
    }
}
