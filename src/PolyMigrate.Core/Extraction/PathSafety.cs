namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 輸出路徑安全檢查(§3.4):slug 來自來源站 URL,不可信任。
/// 驗收標準:同一份 config 在 Windows 與 Linux 產出完全相同的檔案清單——
/// 所以「Windows 會炸的路徑」在任何平台都一致地拒寫並記錄,而不是各平台各自為政。
/// </summary>
internal static class PathSafety
{
    // Windows 保留裝置名(不分大小寫;含副檔名仍保留,如 con.md)
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '\\', '|', '?', '*'];

    /// <summary>逐段檢查('/' 分隔的相對路徑);安全回傳 null,否則回傳問題描述。</summary>
    public static string? Check(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }
            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                return $"reserved device name '{segment}'";
            }
            if (segment.IndexOfAny(InvalidChars) >= 0 || segment.Any(char.IsControl))
            {
                return $"invalid character in '{segment}'";
            }
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return $"trailing dot/space in '{segment}'";
            }
        }
        return null;
    }

    /// <summary>
    /// 輸出路徑碰撞偵測:管線每個來源檔各呼叫一次,故重覆的路徑必為「兩個不同來源檔映到同一輸出」——
    /// 完全相同 = 第二份會靜默覆蓋第一份(如 a.php.html 與 a.asp.html 都收斂成 a.md);
    /// 只差大小寫 = Windows/macOS 不分大小寫檔案系統上會互相覆蓋。兩者都回傳問題描述、拒寫。
    /// </summary>
    public static string? RegisterOrCollide(Dictionary<string, string> seen, string relativePath)
    {
        var key = relativePath.ToLowerInvariant();
        if (seen.TryGetValue(key, out var existing))
        {
            return existing == relativePath
                ? $"duplicate output path (another source also maps to '{relativePath}')"
                : $"case-insensitive collision with '{existing}'";
        }
        seen[key] = relativePath;
        return null;
    }

    /// <summary>超長路徑(Windows 傳統 260 上限)→ warning,不阻斷(long path 已啟用時可用)。</summary>
    public static string? CheckLength(string absolutePath) =>
        absolutePath.Length > 259 ? $"path length {absolutePath.Length} exceeds classic Windows limit (260)" : null;
}
