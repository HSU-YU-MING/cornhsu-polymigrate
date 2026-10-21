using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// config 裡的 CSS selector 語法檢查。
///
/// <para>刻意<b>不</b>在載入 config 時檢查:<c>type_rules</c> 裡寫壞、但從來沒有頁用到的 selector,
/// 1.0 起一直能跑;<c>thumbs</c>、<c>probe-orphans</c> 根本不用 selector。在載入時擋下來,
/// 會讓原本能跑的 config 開始失敗(YAML 面的破壞,要 major)。所以改在「真的要用」之前才檢查——
/// 被擋下的情境,正好就是原本會以 AngleSharp 的 <see cref="DomException"/> 崩潰的那些。</para>
/// </summary>
internal static class CssSelectors
{
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
