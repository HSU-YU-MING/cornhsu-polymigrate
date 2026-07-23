using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 媒體路徑對映(§2.6 %20 雙重編碼坑):磁碟存「解碼後」檔名、輸出網址「單次編碼」。
/// 對映規則與 Phase 1 鏡像一致:原始 URL 路徑(解碼)= media 根目錄下的相對路徑。
/// </summary>
internal sealed class MediaPaths(MediaSection media)
{
    private readonly string _webPrefix = media.WebPrefix.TrimEnd('/') + "/";

    /// <summary>原始資產絕對網址 → media 根目錄下的相對路徑(已解碼、'/' 分隔);根路徑或含穿越分段為 null。</summary>
    public static string? RelativeFromUrl(string absoluteUrl)
    {
        // §3.4:先取 AbsolutePath 再解碼會把 %2e%2e%2f 之類的「編碼後穿越」還原成 ../,
        // 而 Uri 的正規化只吃字面的 ../。解碼後逐段檢查,含 . / .. 或反斜線分段一律拒絕
        // (拒寫 = 當成缺圖記錄,不寫到 media 根目錄外)。
        var path = Uri.UnescapeDataString(new Uri(absoluteUrl).AbsolutePath).TrimStart('/');
        if (path.Length == 0)
        {
            return null;
        }
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment is "." or "..")
            {
                return null;
            }
        }
        return path;
    }

    /// <summary>media 相對路徑 → 磁碟絕對路徑。</summary>
    public static string LocalPath(string mediaRoot, string relative) =>
        Path.Combine(mediaRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>media 相對路徑(已解碼)→ 根絕對網路路徑,逐段單次編碼(空格→%20,'/' 保留)。</summary>
    public string WebPath(string relative) =>
        _webPrefix + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
}
