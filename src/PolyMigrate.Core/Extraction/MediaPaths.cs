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
        // 而 Uri 的正規化只吃字面的 ../。解碼後逐段檢查,含 . / .. / 反斜線 / 冒號的分段一律拒絕
        // (拒寫 = 當成缺圖記錄,不寫到 media 根目錄外)。
        //
        // ⚠ 2026-11-13 修:這裡曾經只擋 . 與 ..,而且檢查的是 Replace 之後的**複本**、
        //   回傳的卻是原文,所以上面那句「含反斜線分段一律拒絕」當時沒有實作。
        //   兩種實測逃得出 media 根目錄的輸入:
        //     /C:/temp/x.png           分段 "C:" 含冒號,Path.Combine 遇到 rooted 的第二段
        //                              會把 mediaRoot 整個丟掉
        //     /%5C%5Chost%5Cs%5Cx.png  解碼後是 UNC 路徑,File.Exists 與寫檔會打到對方的
        //                              SMB 伺服器,Windows 帳號的 NetNTLMv2 雜湊就送出去了
        //   規則與 Inventory/MirrorSlugs.cs 的 EnsurePathSegment 一致:一段目錄名不得含
        //   斜線、反斜線、冒號,不得是 ..。刻意不用 Path.IsPathRooted——它在 Linux 認不出
        //   磁碟機代號,同一個輸入會因平台而有不同結果,而 §3.4 的家規是「Windows 會炸的
        //   路徑在任何平台都一致地拒絕」。
        var path = Uri.UnescapeDataString(new Uri(absoluteUrl).AbsolutePath).TrimStart('/');
        if (path.Length == 0)
        {
            return null;
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or ".." || segment.AsSpan().IndexOfAny('\\', ':') >= 0)
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
