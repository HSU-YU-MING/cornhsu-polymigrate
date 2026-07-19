using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 媒體路徑對映(§2.6 %20 雙重編碼坑):磁碟存「解碼後」檔名、輸出網址「單次編碼」。
/// 對映規則與 Phase 1 鏡像一致:原始 URL 路徑(解碼)= media 根目錄下的相對路徑。
/// </summary>
public sealed class MediaPaths(SiteConfig config)
{
    private readonly string _webPrefix = config.Media.WebPrefix.TrimEnd('/') + "/";

    /// <summary>原始資產絕對網址 → media 根目錄下的相對路徑(已解碼、'/' 分隔);根路徑為 null。</summary>
    public static string? RelativeFromUrl(string absoluteUrl)
    {
        var path = Uri.UnescapeDataString(new Uri(absoluteUrl).AbsolutePath).TrimStart('/');
        return path.Length == 0 ? null : path;
    }

    /// <summary>media 相對路徑 → 磁碟絕對路徑。</summary>
    public static string LocalPath(string mediaRoot, string relative) =>
        Path.Combine(mediaRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>media 相對路徑(已解碼)→ 根絕對網路路徑,逐段單次編碼(空格→%20,'/' 保留)。</summary>
    public string WebPath(string relative) =>
        _webPrefix + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
}
