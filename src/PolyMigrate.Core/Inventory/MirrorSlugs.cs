namespace PolyMigrate.Core.Inventory;

/// <summary>
/// 鏡像裡已經有的 slug(<c>raw/{lang}/{section}/</c> 的檔名去尾綴)。
///
/// 這是「增量同步」刻意只做的那一半。另一半——去舊站讀列表頁、從 HTML 挑出 slug——
/// 沒有做,因為它因站而異(列表頁位置、slug 樣式各不相同,香雲寺的活動甚至沒有列表頁,
/// <c>/ch/events/index.php</c> 回 404,是從首頁卡片連出去的),而且會安靜地壞掉:
/// 舊站一改版、regex 抓不到東西,排程只會每天回報「沒有新文章」。
/// 那一半本來就是十幾行的一次性腳本,留給使用者;這一半只有 PolyMigrate 答得出來。
/// </summary>
internal static class MirrorSlugs
{
    /// <summary>檔名 → slug:<c>20260101.php.html</c> → <c>20260101</c>。</summary>
    private static IEnumerable<string> InDirectory(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(f => Path.GetFileName(f).Split('.')[0])
            : [];

    /// <summary>單一語言的 slug 集(探測時用來跳過已抓過的)。</summary>
    public static HashSet<string> ForLanguage(string rawDir, string langPrefix, string section) =>
        [.. InDirectory(Path.Combine(rawDir, langPrefix, section))];

    /// <summary>
    /// 列出 section 底下的 slug,字典序、跨語言去重。
    /// <paramref name="langPrefix"/> = null 時涵蓋 raw/ 下所有語言目錄。
    /// </summary>
    /// <returns>
    /// 找不到任何對應目錄時回傳 <c>null</c>——這與「目錄在、但裡面沒有頁」必須分得開:
    /// 前者幾乎都是 section 名打錯,而靜靜回一份空清單會被當成「舊站沒有新文章」,
    /// 正是排程任務最要命的失敗方式。
    /// </returns>
    public static IReadOnlyList<string>? List(string rawDir, string section, string? langPrefix)
    {
        if (!Directory.Exists(rawDir))
        {
            return null;
        }
        var langs = langPrefix is null
            ? Directory.EnumerateDirectories(rawDir).Select(d => Path.GetFileName(d)).ToList()
            : [langPrefix];

        var slugs = new SortedSet<string>(StringComparer.Ordinal);
        var sectionFound = false;
        foreach (var lang in langs)
        {
            var dir = Path.Combine(rawDir, lang, section);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            sectionFound = true;
            slugs.UnionWith(InDirectory(dir));
        }
        return sectionFound ? [.. slugs] : null;
    }
}
