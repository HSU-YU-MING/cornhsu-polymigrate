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

/// <summary>nginx:<c>location = {old} { return 301 {new}; }</c>。</summary>
internal sealed class NginxRedirectExporter : IRedirectExporter
{
    public string FileName => "redirects.nginx.conf";

    public string Render(IReadOnlyList<(string Old, string New)> pairs) =>
        string.Join('\n', pairs.Select(p => $"location = {p.Old} {{ return 301 {p.New}; }}")) + "\n";
}

/// <summary>Netlify <c>_redirects</c>:<c>{old} {new} 301</c>。</summary>
internal sealed class NetlifyRedirectExporter : IRedirectExporter
{
    public string FileName => "_redirects";

    public string Render(IReadOnlyList<(string Old, string New)> pairs) =>
        string.Join('\n', pairs.Select(p => $"{p.Old} {p.New} 301")) + "\n";
}
