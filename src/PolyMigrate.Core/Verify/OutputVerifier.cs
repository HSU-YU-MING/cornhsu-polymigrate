using System.Text;
using System.Text.RegularExpressions;
using PolyMigrate.Core.Diagnostics;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Inventory;
using YamlDotNet.Serialization;

namespace PolyMigrate.Core.Verify;

/// <summary>一筆巡檢發現。severity:error(阻斷發布)/ warning(已知或可接受,記錄)。</summary>
public sealed record VerifyIssue(Severity Severity, string Page, string Kind, string Detail);

public sealed class VerifyReport
{
    public required List<VerifyIssue> Issues { get; init; }

    public int PagesChecked { get; init; }

    public int LinksChecked { get; init; }

    public int MediaChecked { get; init; }

    public bool MediaChecksSkipped { get; init; }

    public int Errors => Issues.Count(i => i.Severity == Severity.Error);

    public int Warnings => Issues.Count(i => i.Severity == Severity.Warning);
}

/// <summary>
/// 全站巡檢(§3.6):只讀 Phase 2 輸出(content/ 與清單),不碰網路與鏡像——契約完整性的試金石。
/// 檢查 frontmatter 必填欄位、內部連結對路由集、媒體引用對磁碟;
/// missing_images.csv 已記錄的原站壞圖降為 warning(已知、非搬遷回歸)。
/// </summary>
public sealed partial class OutputVerifier
{
    private static readonly string[] RequiredFields =
        ["source_url", "lang", "slug", "translation_key", "title", "page_type"];

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <param name="allowUnlinked">
    /// 豁免孤島偵測的頁(content 相對路徑或路由皆可)。刻意不從 config 讀:
    /// verify 的「只讀輸出、不需 config」是它的設計前提,不為了一份清單破例。
    /// </param>
    public VerifyReport Run(string outDir, string? mediaDir, string mediaPrefix = "/media/",
        IReadOnlyCollection<string>? allowUnlinked = null)
    {
        var contentDir = Path.Combine(outDir, "content");
        if (!Directory.Exists(contentDir))
        {
            return new VerifyReport
            {
                Issues = [new VerifyIssue(Severity.Error, "", "no_content", $"content directory not found: {contentDir}")],
            };
        }
        var checkMedia = mediaDir is not null && Directory.Exists(mediaDir);

        var files = Directory.EnumerateFiles(contentDir, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetRelativePath(contentDir, f).Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();
        var routes = BuildRoutes(contentDir, files);
        var knownMissing = LoadKnownMissing(outDir);

        var issues = new List<VerifyIssue>();
        var linksChecked = 0;
        var mediaChecked = 0;
        // 孤島偵測用:被任何內容頁引用到的路由,以及每頁自己的路由/頁型
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var pageRoutes = new List<PageRoute>();

        foreach (var file in files)
        {
            var page = Path.GetRelativePath(contentDir, file).Replace('\\', '/');
            var text = File.ReadAllText(file).Replace("\r", "");

            if (SplitFrontmatter(text) is not var (yaml, body))
            {
                issues.Add(new VerifyIssue(Severity.Error, page, "invalid_frontmatter", "no frontmatter block"));
                continue;
            }
            Dictionary<string, object?>? fm;
            try
            {
                fm = Yaml.Deserialize<Dictionary<string, object?>>(yaml);
            }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException)
            {
                issues.Add(new VerifyIssue(Severity.Error, page, "invalid_frontmatter", ex.Message));
                continue;
            }
            foreach (var field in RequiredFields)
            {
                if (fm?.GetValueOrDefault(field) is not string s || s.Length == 0)
                {
                    issues.Add(new VerifyIssue(Severity.Error, page, "missing_field", field));
                }
            }
            var selfRoute = RouteFor(page);
            pageRoutes.Add(new PageRoute(
                page,
                selfRoute,
                fm?.GetValueOrDefault("page_type") as string ?? "",
                page == "index.md" || page.EndsWith("/index.md", StringComparison.Ordinal)));

            // frontmatter images[].local 也要驗(相簿頁型的圖不在內文)
            var refs = new List<string>(ExtractInternalRefs(body));
            if (fm?.GetValueOrDefault("images") is IEnumerable<object> images)
            {
                foreach (var img in images)
                {
                    if (img is IDictionary<object, object> d && d.TryGetValue("local", out var local)
                        && local is string localStr)
                    {
                        refs.Add(localStr);   // 手改壞的 frontmatter 可能讓 local 非字串,略過而非崩潰
                    }
                }
            }

            foreach (var reference in refs)
            {
                if (reference.StartsWith(mediaPrefix, StringComparison.Ordinal))
                {
                    if (!checkMedia)
                    {
                        continue;
                    }
                    mediaChecked++;
                    // 去掉 ?query / #fragment 再對磁碟找檔(/media/x.jpg?v=2 的 ?v=2 不是檔名一部分)
                    var clean = reference.Split('#')[0].Split('?')[0];
                    var rel = Uri.UnescapeDataString(clean[mediaPrefix.Length..]);
                    if (!File.Exists(Path.Combine(mediaDir!, rel.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        issues.Add(knownMissing.Contains(clean)
                            ? new VerifyIssue(Severity.Warning, page, "known_missing_media", reference)
                            : new VerifyIssue(Severity.Error, page, "missing_media", reference));
                    }
                }
                else
                {
                    linksChecked++;
                    var route = ResolveRoute(reference, routes);
                    if (route != selfRoute)
                    {
                        // 反向:沒進這個集合的路由 = 沒有任何頁連得到。
                        // 自己連自己不算入口——訪客還是得先有辦法到這一頁才看得到那個連結,
                        // 否則一個 canonical 或麵包屑的自我連結就會讓這頁靜靜地逃掉偵測。
                        referenced.Add(route);
                    }
                    if (!routes.Contains(route))
                    {
                        issues.Add(new VerifyIssue(Severity.Error, page, "broken_link", reference));
                    }
                }
            }
        }

        issues.AddRange(FindOrphans(pageRoutes, referenced, allowUnlinked));

        WriteReport(outDir, issues);
        return new VerifyReport
        {
            Issues = issues,
            PagesChecked = files.Count,
            LinksChecked = linksChecked,
            MediaChecked = mediaChecked,
            MediaChecksSkipped = !checkMedia,
        };
    }

    private static (string Yaml, string Body)? SplitFrontmatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? null : (text[4..(end + 1)], text[(end + 5)..]);
    }

    /// <summary>content 樹 → 路由集:{prefix}/{section}/{slug};index 檔代表其目錄路由。</summary>
    private static HashSet<string> BuildRoutes(string contentDir, List<string> files)
    {
        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            routes.Add(RouteFor(Path.GetRelativePath(contentDir, file).Replace('\\', '/')));
        }
        return routes;
    }

    /// <summary>content 相對路徑(如 en/blia/index.md)→ 路由(/en/blia)。</summary>
    private static string RouteFor(string relativeMarkdownPath)
    {
        var rel = relativeMarkdownPath[..^".md".Length];
        return NormalizeRoute(rel.EndsWith("/index", StringComparison.Ordinal)
            ? "/" + rel[..^"/index".Length]
            : rel == "index" ? "/" : "/" + rel);
    }

    /// <summary>一頁的孤島判定所需資訊。</summary>
    private sealed record PageRoute(string Page, string Route, string PageType, bool IsIndex);

    /// <summary>
    /// 孤島頁面:輸出裡有這一頁,卻沒有任何內容頁連到它——訪客除非自己打網址,否則到不了。
    /// 判 warning 而非 error:有些頁本來就刻意不連(活動到達頁、隱私頁)。
    ///
    /// 界線要講清楚:這裡只看得到「內容頁之間」的連結。導覽選單不在 Phase 2 輸出裡,
    /// verify 看不到它,所以能斷言的是「沒有內容頁連到它」,不是「完全沒有入口」——
    /// 訊息措辭必須跟得上這個界線,否則只是換一種形式的過度信任。
    /// </summary>
    private static IEnumerable<VerifyIssue> FindOrphans(
        List<PageRoute> pages, HashSet<string> referenced, IReadOnlyCollection<string>? allowUnlinked)
    {
        var allowed = new HashSet<string>(allowUnlinked ?? [], StringComparer.Ordinal);
        return pages
            .Where(p => !referenced.Contains(p.Route)
                && !IsExemptByDefault(p)
                && !allowed.Contains(p.Page)
                && !allowed.Contains(p.Route))
            .Select(p => new VerifyIssue(Severity.Warning, p.Page, "unlinked_page",
                "not linked from any content page (menus are not checked)"));
    }

    /// <summary>
    /// 內建豁免。沒有這一段,這個檢查會因為誤報太多而失去可信度——而一個沒人看的警告
    /// 比沒有警告更糟(它會稀釋整份巡檢報告)。兩類本來就不會被內容頁連到的頁:
    /// <list type="bullet">
    /// <item>站根與各語言首頁:深度 ≤ 1 的目錄索引,沒有人會連回去。</item>
    /// <item>分類列表頁(page_type: listing):一般只從選單進入,而選單不在掃描範圍。</item>
    /// </list>
    /// 刻意<b>不</b>豁免深度 ≥ 2 的目錄索引——香雲寺 BLIA 那類「頂層單頁」正是要抓的目標,
    /// 它與列表頁的差別在於所屬 section 未宣告型別,故 page_type 是 page 而非 listing。
    ///
    /// <para>已知限制(留帳):單語站(無語言前綴)的頂層 section 索引也落在深度 1,會一併
    /// 被豁免。<b>利息</b>:BLIA 那一類頁在單語站上抓不到,也就是這個檢查對單語站的效果打折。
    /// 要還這筆帳得先讓 verify 分得出「語言目錄」與「一般目錄」,而那需要 config——
    /// 會破壞 verify 不需 config 的前提,所以刻意先欠著。寧可漏報也不誤報:
    /// 漏的那類人工巡檢補得回來,誤報會讓人整份報告都不看。</para>
    /// </summary>
    private static bool IsExemptByDefault(PageRoute page) =>
        (page.IsIndex && Depth(page.Route) <= 1)
        // 綁到常數而非字面字串:頁型名稱改了要嘛兩邊一起改、要嘛編譯不過。
        // 各自寫死的話,verify 會安靜地停止豁免所有列表頁,然後每個多語站都爆出一堆警告。
        || page.PageType == PageClassifier.Listing;

    private static int Depth(string route) => route == "/" ? 0 : route.Count(c => c == '/');

    /// <summary>
    /// 連結 → 路由。原樣對不上時再試一次百分號解碼版:瀏覽器與編輯器產出中日韓 href
    /// 預設是編碼的(<c>/ch/news/%E7%A6%AA%E4%BF%AE</c>),而鏡像檔名是解碼的(§2.6)——
    /// 直接比字串會把「存在、而且真的被連到」的頁判成壞連結,並連帶誤報成孤島頁面。
    /// 對這個 i18n-first 的工具來說,那是旗艦情境而不是邊角。
    /// **只在對不上時才解碼**,原本就對得上的維持逐位元組不變(golden 不動)。
    /// </summary>
    private static string ResolveRoute(string reference, HashSet<string> routes)
    {
        var route = NormalizeRoute(reference);
        if (routes.Contains(route))
        {
            return route;
        }
        var decoded = NormalizeRoute(Uri.UnescapeDataString(reference));
        return routes.Contains(decoded) ? decoded : route;
    }

    /// <summary>連結正規化:去 #fragment/?query、去尾斜線(根除外)。</summary>
    private static string NormalizeRoute(string link)
    {
        var l = link.Split('#')[0].Split('?')[0].TrimEnd('/');
        return l.Length == 0 ? "/" : l;
    }

    /// <summary>抽正文裡的站內引用:markdown 連結/圖片 與內嵌 HTML 的 href/src,只留 "/" 開頭者。</summary>
    private static IEnumerable<string> ExtractInternalRefs(string body)
    {
        foreach (Match m in MarkdownTarget().Matches(body))
        {
            if (m.Groups[1].Value is ['/', not '/', ..])
            {
                yield return m.Groups[1].Value;
            }
        }
        foreach (Match m in HtmlTarget().Matches(body))
        {
            var value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (value is ['/', not '/', ..])
            {
                yield return value;
            }
        }
    }

    private static HashSet<string> LoadKnownMissing(string outDir)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(outDir, "missing_images.csv");
        if (File.Exists(path))
        {
            foreach (var row in Csv.ReadRows(path).Skip(1))
            {
                if (row.Count >= 2)
                {
                    known.Add(row[1]);
                }
            }
        }
        return known;
    }

    private static void WriteReport(string outDir, List<VerifyIssue> issues)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "severity", "page", "kind", "detail" },
        };
        rows.AddRange(issues
            .OrderBy(i => i.Severity.Wire(), StringComparer.Ordinal)
            .ThenBy(i => i.Page, StringComparer.Ordinal)
            .ThenBy(i => i.Detail, StringComparer.Ordinal)
            .Select(i => new[] { i.Severity.Wire(), i.Page, i.Kind, i.Detail }));
        Csv.Write(Path.Combine(outDir, "verify_report.csv"), rows);
    }

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownTarget();

    [GeneratedRegex("""(?:href|src)\s*=\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTarget();
}
