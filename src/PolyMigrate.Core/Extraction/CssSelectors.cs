using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// config 裡的 CSS selector 語法檢查。
///
/// <para>兩道防線。第一道在<b>載入 config 時</b>(<see cref="SiteConfigLoader"/>,3.0 起):所有 selector 欄位都驗,
/// 包括這次沒有頁屬於那個頁型的 <c>type_rules</c>,以及根本不用 selector 的指令(<c>thumbs</c>、<c>probe-orphans</c>)
/// 讀到的 config——寫壞的 config 不該等到某個指令、某個頁型才炸。2.x 刻意不在載入時驗,只是為了不讓原本能跑的
/// config 在 patch 版開始失敗。第二道在「真的要用」之前(<see cref="Ensure"/>、<see cref="QueryAll"/>):
/// 函式庫呼叫端可以不經 loader、直接在程式裡組 <see cref="SiteConfig"/>,這一層保證那種 config 寫壞時
/// 也是 <see cref="ConfigException"/>,而不是 AngleSharp 的 <see cref="DomException"/> 半途崩潰。</para>
/// </summary>
internal static class CssSelectors
{
    /// <summary>
    /// <paramref name="selector"/> 合語法回 null,否則回點名欄位與 selector 的訊息(與 <see cref="Ensure"/> 擲出的相同)。
    /// 給載入 config 時一次收集所有錯誤用。
    /// </summary>
    public static string? Problem(string selector, string field)
    {
        try
        {
            Ensure(selector, field);
            return null;
        }
        catch (ConfigException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// <paramref name="selector"/> 不合語法時擲 <see cref="ConfigException"/>,訊息點名欄位
    /// (CLI 原樣印出、exit 2)。拿空文件跑一次 QuerySelector:語法錯誤與文件內容無關,
    /// 而且這樣走的就是真正用到它時的同一條解析路徑,不會出現「檢查過了、用的時候還是爆」。
    /// </summary>
    public static void Ensure(string selector, string field)
    {
        try
        {
            _ = new HtmlParser().ParseDocument("").QuerySelector(selector);
        }
        catch (DomException ex)
        {
            throw Invalid(selector, field, ex);
        }
    }

    /// <summary>
    /// 等同 <c>root.QuerySelectorAll(selector)</c>,但語法錯誤時擲 <see cref="ConfigException"/>
    /// (給沒辦法事先知道會不會用到、只能在用的當下檢查的呼叫端)。
    /// </summary>
    public static IHtmlCollection<IElement> QueryAll(IParentNode root, string selector, string field)
    {
        try
        {
            return root.QuerySelectorAll(selector);
        }
        catch (DomException ex)
        {
            throw Invalid(selector, field, ex);
        }
    }

    private static ConfigException Invalid(string selector, string field, DomException inner) =>
        new($"{field}: '{selector}' is not a valid CSS selector.", inner);
}
