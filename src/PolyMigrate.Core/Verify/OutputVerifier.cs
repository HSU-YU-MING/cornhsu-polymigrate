using System.Text;
using System.Text.RegularExpressions;
using PolyMigrate.Core.Inventory;
using YamlDotNet.Serialization;

namespace PolyMigrate.Core.Verify;

/// <summary>一筆巡檢發現。severity:error(阻斷發布)/ warning(已知或可接受,記錄)。</summary>
public sealed record VerifyIssue(string Severity, string Page, string Kind, string Detail);

public sealed class VerifyReport
{
    public required List<VerifyIssue> Issues { get; init; }

    public int PagesChecked { get; init; }

    public int LinksChecked { get; init; }

    public int MediaChecked { get; init; }

    public bool MediaChecksSkipped { get; init; }

    public int Errors => Issues.Count(i => i.Severity == "error");

    public int Warnings => Issues.Count(i => i.Severity == "warning");
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

    public VerifyReport Run(string outDir, string? mediaDir, string mediaPrefix = "/media/")
    {
        var contentDir = Path.Combine(outDir, "content");
        if (!Directory.Exists(contentDir))
        {
            return new VerifyReport
            {
                Issues = [new VerifyIssue("error", "", "no_content", $"content directory not found: {contentDir}")],
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

        foreach (var file in files)
        {
            var page = Path.GetRelativePath(contentDir, file).Replace('\\', '/');
            var text = File.ReadAllText(file).Replace("\r", "");

            if (SplitFrontmatter(text) is not var (yaml, body))
            {
                issues.Add(new VerifyIssue("error", page, "invalid_frontmatter", "no frontmatter block"));
                continue;
            }
            Dictionary<string, object?>? fm;
            try
            {
                fm = Yaml.Deserialize<Dictionary<string, object?>>(yaml);
            }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException)
            {
                issues.Add(new VerifyIssue("error", page, "invalid_frontmatter", ex.Message));
                continue;
            }
            foreach (var field in RequiredFields)
            {
                if (fm?.GetValueOrDefault(field) is not string s || s.Length == 0)
                {
                    issues.Add(new VerifyIssue("error", page, "missing_field", field));
                }
            }

            // frontmatter images[].local 也要驗(相簿頁型的圖不在內文)
            var refs = new List<string>(ExtractInternalRefs(body));
            if (fm?.GetValueOrDefault("images") is IEnumerable<object> images)
            {
                foreach (var img in images)
                {
                    if (img is IDictionary<object, object> d && d.TryGetValue("local", out var local))
                    {
                        refs.Add((string)local!);
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
                    var rel = Uri.UnescapeDataString(reference[mediaPrefix.Length..]);
                    if (!File.Exists(Path.Combine(mediaDir!, rel.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        issues.Add(knownMissing.Contains(reference)
                            ? new VerifyIssue("warning", page, "known_missing_media", reference)
                            : new VerifyIssue("error", page, "missing_media", reference));
                    }
                }
                else
                {
                    linksChecked++;
                    if (!routes.Contains(NormalizeRoute(reference)))
                    {
                        issues.Add(new VerifyIssue("error", page, "broken_link", reference));
                    }
                }
            }
        }

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
            var rel = Path.GetRelativePath(contentDir, file).Replace('\\', '/')[..^".md".Length];
            routes.Add(NormalizeRoute(rel.EndsWith("/index", StringComparison.Ordinal)
                ? "/" + rel[..^"/index".Length]
                : rel == "index" ? "/" : "/" + rel));
        }
        return routes;
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
            if (m.Groups[1].Value is ['/', not '/', ..])
            {
                yield return m.Groups[1].Value;
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
            .OrderBy(i => i.Severity, StringComparer.Ordinal)
            .ThenBy(i => i.Page, StringComparer.Ordinal)
            .ThenBy(i => i.Detail, StringComparer.Ordinal)
            .Select(i => new[] { i.Severity, i.Page, i.Kind, i.Detail }));
        Csv.Write(Path.Combine(outDir, "verify_report.csv"), rows);
    }

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownTarget();

    [GeneratedRegex(@"(?:href|src)=""([^""]+)""")]
    private static partial Regex HtmlTarget();
}
