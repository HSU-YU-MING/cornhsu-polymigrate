namespace PolyMigrate.Core.Redirects;

/// <summary>
/// 把 (舊路徑 → 新路徑) 301 對映輸出成某種可直接部署的設定檔格式。
/// 新增一種輸出 = 加一個實作進 <see cref="RedirectExporter.All"/>,不必動 pipeline。
/// </summary>
internal interface IRedirectExporter
{
    /// <summary>輸出檔名(相對於輸出根目錄)。</summary>
    string FileName { get; }

    /// <summary>把已排序、已去自我轉址的對映渲染成該格式的完整檔案內容(含結尾換行)。</summary>
    string Render(IReadOnlyList<(string Old, string New)> pairs);
}

internal static class RedirectExporter
{
    /// <summary>目前支援的 301 輸出格式。</summary>
    public static readonly IRedirectExporter[] All =
        [new NginxRedirectExporter(), new NetlifyRedirectExporter()];
}

/// <summary>
/// nginx:<c>location = {old} { return 301 {new}; }</c>。
///
/// <para><b>{old} 寫解碼後的路徑。</b>nginx 的 location 比對的是正規化後的 URI——先把 <c>%XX</c> 解碼、
/// 解析 <c>.</c> 與 <c>..</c>、合併斜線(ngx_http_core_module 文件「location」段);設定檔裡的
/// location 字串則照字面。寫成 <c>/ch/news/%E7%A6%AA%E4%BF%AE.php</c> 的話,只有解碼後字面就是
/// <c>%E7…</c> 的請求才對得上——中日韓或含空白的舊網址永遠不會 301,直接 404。</para>
///
/// <para><b>{new} 維持編碼</b>(來自 <c>Uri.AbsolutePath</c>):它是 Location 標頭的值,應為 ASCII。
/// <c>return</c> 的值會展開 <c>$變數</c>(加引號也一樣),所以 <c>$</c> 一律編碼成 <c>%24</c>。</para>
///
/// <para>兩邊都只在必要時加雙引號(空白、<c>; { } " ' \ #</c>、控制字元),其餘維持原樣——
/// 純 ASCII 的一般路徑與舊版逐位元組相同。排序依寫出來的(解碼後的)路徑。</para>
/// </summary>
internal sealed class NginxRedirectExporter : IRedirectExporter
{
    public string FileName => "redirects.nginx.conf";

    public string Render(IReadOnlyList<(string Old, string New)> pairs) =>
        string.Join('\n', pairs
            .Select(p => (Old: Uri.UnescapeDataString(p.Old), p.New))
            .OrderBy(p => p.Old, StringComparer.Ordinal)
            .Select(p => $"location = {Token(p.Old)} {{ return 301 {Token(p.New.Replace("$", "%24"))}; }}")) + "\n";

    /// <summary>nginx 設定檔的一個參數:會被 nginx 斷開或誤讀的字元出現時才加雙引號並跳脫。</summary>
    private static string Token(string value)
    {
        if (!value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ';' or '{' or '}' or '"' or '\'' or '\\' or '#' or '$'))
        {
            return value;
        }
        var quoted = new System.Text.StringBuilder("\"");
        foreach (var c in value)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\t' => "\\t",
                '\r' => "\\r",
                '\n' => "\\n",
                _ => c.ToString(),
            });
        }
        return quoted.Append('"').ToString();
    }
}

/// <summary>Netlify <c>_redirects</c>:<c>{old} {new} 301</c>。</summary>
internal sealed class NetlifyRedirectExporter : IRedirectExporter
{
    public string FileName => "_redirects";

    public string Render(IReadOnlyList<(string Old, string New)> pairs) =>
        string.Join('\n', pairs.Select(p => $"{p.Old} {p.New} 301")) + "\n";
}
