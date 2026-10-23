namespace PolyMigrate.Core.Orphans;

/// <summary>
/// 舊站上的「語言前綴/section/slug」路徑。
///
/// <para>語言前綴可以是空字串(<c>lang_map</c> 的 key 是 <c>""</c> = 沒有語言前綴的站,
/// 見 <see cref="Inventory.MirrorSlugs"/>)。直接 <c>$"{base}/{lang}/{section}/…"</c> 會組出
/// <c>//news/…</c>:nginx 預設合併斜線所以看不出來,但不合併斜線的伺服器或框架路由會讓
/// probe 全部落空、fetch 全部 404。所以只接非空的段。</para>
/// </summary>
internal static class SitePaths
{
    /// <summary>非空的段以 <c>/</c> 連接(不加開頭斜線):<c>("", "news", "a")</c> → <c>news/a</c>。</summary>
    public static string Join(params string[] segments) =>
        string.Join('/', segments.Where(s => s.Length > 0));
}
