using AngleSharp.Html.Parser;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Orphans;

public sealed class FetchReport
{
    public int PagesFetched { get; set; }

    public int PagesSkipped { get; set; }

    public int AssetsFetched { get; set; }

    public List<string> Errors { get; } = [];
}

/// <summary>
/// 抓回孤兒頁(規格 fetch_orphans.py):每個 slug 抓所有語言版進 raw/,
/// 正文引用的圖片/資產進 media/(路徑鏡像、解碼檔名 §2.6)。
/// 與規格的差異:頁面存「原始 bytes」而非解碼字串——保留來源編碼,
/// 之後抽取統一依 config site.encoding 解碼(§3.1)。
/// </summary>
public sealed class OrphanFetcher(SiteConfig config, HttpClient http)
{
    private static readonly string[] AssetExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".mp4"];

    private readonly string _baseUrl = config.Site.BaseUrl.TrimEnd('/');
    private readonly string _extension = config.UrlPattern.StripExtensions.FirstOrDefault() ?? "";
    private readonly HtmlParser _parser = new();

    public async Task<FetchReport> FetchAsync(
        IReadOnlyList<string> slugs, string section, string rawDir, string mediaDir,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var report = new FetchReport();
        foreach (var slug in slugs)
        {
            foreach (var langPrefix in config.UrlPattern.LangMap.Keys)
            {
                var url = $"{_baseUrl}/{langPrefix}/{section}/{slug}{_extension}";
                var rawPath = Path.Combine(rawDir, langPrefix, section, $"{slug}{_extension}.html");
                if (File.Exists(rawPath))
                {
                    report.PagesSkipped++;
                    continue;
                }
                await Task.Delay(config.Site.Polite.DelayMs, ct);
                byte[] bytes;
                try
                {
                    using var response = await http.GetAsync(url, ct);
                    if (response.StatusCode != System.Net.HttpStatusCode.OK
                        || response.Content.Headers.ContentType?.MediaType?.Contains("html") != true)
                    {
                        report.Errors.Add($"[{(int)response.StatusCode}] {langPrefix}/{section}/{slug}");
                        continue;
                    }
                    bytes = await response.Content.ReadAsByteArrayAsync(ct);
                }
                catch (Exception ex) when (IsTransient(ex, ct))
                {
                    // 連線層錯誤或逾時(非使用者取消):記錄該頁、繼續下一頁,不掀掉整批
                    report.Errors.Add($"[err] {url} {ex.Message}");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
                await WriteAtomicAsync(rawPath, bytes, ct);
                report.PagesFetched++;
                progress?.Invoke($"fetched {langPrefix}/{section}/{slug}");

                await FetchAssetsAsync(url, bytes, mediaDir, report, ct);
            }
        }
        return report;
    }

    private async Task FetchAssetsAsync(string pageUrl, byte[] pageBytes, string mediaDir,
        FetchReport report, CancellationToken ct)
    {
        var html = TextEncodings.Resolve(config.Site.Encoding).GetString(pageBytes);
        var doc = _parser.ParseDocument(html);
        var nodes = doc.QuerySelectorAll(config.Extract.Content);

        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            foreach (var el in node.QuerySelectorAll("img[src], source[src]"))
            {
                sources.Add(el.GetAttribute("src")!);
            }
        }

        var host = new Uri(config.Site.BaseUrl).Host;
        foreach (var src in sources)
        {
            Uri absolute;
            try
            {
                absolute = new Uri(new Uri(pageUrl), src);
            }
            catch (UriFormatException)
            {
                continue;
            }
            if (!string.Equals(absolute.Host, host, StringComparison.OrdinalIgnoreCase)
                || !AssetExtensions.Contains(Path.GetExtension(absolute.AbsolutePath).ToLowerInvariant())
                || MediaPaths.RelativeFromUrl(absolute.AbsoluteUri) is not { } relative)
            {
                continue;
            }
            var local = MediaPaths.LocalPath(mediaDir, relative);
            if (File.Exists(local))
            {
                continue;
            }
            await Task.Delay(config.Site.Polite.DelayMs, ct);
            try
            {
                using var response = await http.GetAsync(absolute, ct);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    continue;
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length == 0)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await WriteAtomicAsync(local, bytes, ct);
                report.AssetsFetched++;
            }
            catch (Exception ex) when (IsTransient(ex, ct))
            {
                // 單一資產失敗或逾時不阻斷(可能就是原站壞圖或慢伺服器)
            }
        }
    }

    /// <summary>HTTP 層錯誤或請求逾時(非呼叫端主動取消)才算可略過的暫時性失敗。</summary>
    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>先寫暫存檔再原子改名:寫到一半被中斷不會在最終路徑留下半截檔(重跑會當它已完成)。</summary>
    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 失敗/取消時清掉半截暫存檔,別留垃圾
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }
}
