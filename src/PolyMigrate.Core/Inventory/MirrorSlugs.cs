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
    /// <summary>
    /// 檔名 → slug:<c>20260101.php.html</c> → <c>20260101</c>。
    /// 只認 <c>*.html</c>——這正是 ExtractionPipeline 對「一個鏡像頁」的定義,同一把尺。
    /// 逐檔取名的話,鏡像目錄被 Finder / 檔案總管開過長出來的 .DS_Store 會變成空字串 slug
    /// (輸出第一行就是空行),Thumbs.db 會變成一個叫「Thumbs」的假文章——
    /// 而這個指令的賣點就是 stdout 乾淨到可以直接導成檔案再餵給 fetch-orphans。
    /// </summary>
    private static IEnumerable<string> InDirectory(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.html")
                .Select(f => Path.GetFileName(f).Split('.')[0])
                .Where(slug => slug.Length > 0)
            : [];

    /// <summary>
    /// section / lang 是「一段目錄名」,不是路徑。<c>Path.Combine</c> 只要後段是絕對路徑
    /// 就會把前段整個丟掉——所以 <c>--lang C:\somewhere</c> 會安靜地去讀鏡像目錄以外的地方,
    /// 而且照樣回報成功。在本機 CLI 上這不是提權(使用者本來就讀得到自己的磁碟),
    /// 該擋的理由是**它做了你沒要求的事卻不出聲**,與「section 打錯回錯誤而非空清單」同一個原則。
    ///
    /// <para>判斷方式刻意不用 <see cref="Path.IsPathRooted(string)"/>:它在 Linux 上認不出
    /// <c>C:\Windows</c>(冒號與反斜線在那裡都只是普通字元),同一個輸入會因平台而有不同結果——
    /// 而 §3.4 的家規是「Windows 會炸的路徑在任何平台都一致地拒絕」,見 PathSafety。
    /// 改為直接要求「一段目錄名」:不得含 <c>/</c>、<c>\</c>、<c>:</c>,不得是 <c>..</c>。
    /// 空字串合法(無語言前綴的站)。</para>
    /// </summary>
    private static void EnsurePathSegment(string value, string name)
    {
        if (value == ".." || value.AsSpan().IndexOfAny('/', '\\', ':') >= 0)
        {
            throw new ArgumentException(
                $"{name} must be a single directory name (no path separators), got '{value}'.");
        }
    }

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
        EnsurePathSegment(section, "section");
        if (langPrefix is not null)
        {
            EnsurePathSegment(langPrefix, "lang");
        }
        if (!Directory.Exists(rawDir))
        {
            return null;
        }
        var langs = new List<string>();
        if (langPrefix is null)
        {
            langs.AddRange(new DirectoryInfo(rawDir).EnumerateDirectories().Select(d => d.Name));
            // 無語言前綴的站(lang_map 的 key 是 "",見 RawPage.LangPrefix)其 section
            // 直接在 raw/ 底下,不在任何語言目錄裡。只掃語言目錄會把 raw/news 當成
            // 語言 "news" 再去找 raw/news/news,於是這類站一筆都找不到。
            langs.Add("");
        }
        else
        {
            langs.Add(langPrefix);
        }

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
