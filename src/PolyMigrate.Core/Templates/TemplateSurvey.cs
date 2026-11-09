using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Templates;

/// <summary>一個鏡像頁在模板分群裡的結果。</summary>
/// <param name="ContentMatches"><c>extract.content</c> 在這頁選到幾個元素;0 = extract 會寫出只有 frontmatter 的空頁。</param>
internal sealed record TemplatePage(
    string Page, string Section, string Locale, int Template, int ContentMatches, int ContentChars, int BodyChars, string? Candidate);

/// <summary>一個模板(版面骨架相近的一群頁)。<see cref="Id"/> 依頁數由多到少編號,從 1 起。</summary>
internal sealed record TemplateGroup(int Id, List<TemplatePage> Pages)
{
    /// <summary><c>extract.content</c> 一個元素都沒選到的頁數。選到了但沒有字(只有圖的活動海報頁)不算——那是內容本來就是圖。</summary>
    public int ContentEmpty => Pages.Count(p => p.ContentMatches == 0);

    /// <summary><c>extract.content</c> 抓到的字數占整頁字數的中位數(0～100);整頁沒有字的頁不算。</summary>
    public int? ContentCoverage
    {
        get
        {
            var ratios = Pages.Where(p => p.BodyChars > 0)
                .Select(p => Math.Min(100, (int)Math.Round(100.0 * p.ContentChars / p.BodyChars)))
                .Order().ToList();
            return ratios.Count == 0 ? null : ratios[ratios.Count / 2];
        }
    }

    /// <summary>這群頁最常見的內容區候選,以及有幾頁是它。沒有任何頁找得到候選時是 null。</summary>
    public (string Selector, int Pages)? Suggested =>
        Pages.Where(p => p.Candidate is not null)
            .GroupBy(p => p.Candidate!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => ((string, int)?)(g.Key, g.Count()))
            .FirstOrDefault();
}

internal sealed class TemplateSurveyReport
{
    public required List<TemplateGroup> Groups { get; init; }

    public int PagesSurveyed => Groups.Sum(g => g.Pages.Count);

    public int ContentEmpty => Groups.Sum(g => g.ContentEmpty);
}

/// <summary>
/// 模板分群(<c>polymigrate templates</c>):寫 config 之前,先知道舊站有幾種版面、<c>extract.content</c> 在每一種上抓不抓得到東西。
/// <c>extract.content</c> 只有一個 selector,舊站卻常有好幾種模板(首頁、列表、文章、活動、舊版殘留…)——
/// 原本要整份 extract 跑完、一頁一頁看 Markdown 才知道哪一種漏了。
/// <para>分群看的是<b>版面骨架</b>:body 底下前幾層的版面元素(div、section、nav…)連同 id 與第一個 class 的路徑集合,
/// 兩頁的集合 Jaccard 相似度 ≥ <see cref="SameTemplate"/> 就算同一種。id / class 裡的數字一律視為相同
/// (<c>post-123</c> 與 <c>post-456</c> 是同一種模板)。純本地、只讀鏡像,不碰網路、不寫 content/。</para>
/// </summary>
internal sealed partial class TemplateSurvey(SiteConfig config)
{
    /// <summary>同一種模板的門檻:骨架路徑集合的 Jaccard 相似度。</summary>
    internal const double SameTemplate = 0.8;

    /// <summary>骨架看到 body 底下第幾層為止:再往下是內文本身,每頁都不一樣。</summary>
    private const int SkeletonDepth = 4;

    /// <summary>內容區候選:字數至少占整頁這麼多的元素裡,最深的那一個。</summary>
    private const double ContentShare = 0.5;

    private static readonly HashSet<string> LayoutTags = new(StringComparer.Ordinal)
    {
        "div", "section", "main", "article", "header", "footer", "nav", "aside", "table", "form", "ul", "ol",
    };

    private readonly HtmlParser _parser = new();

    /// <summary>測試用:每頁之前呼叫一次(<c>page:&lt;raw 相對路徑&gt;</c>),讓測試能在半途取消。</summary>
    internal Action<string>? OnCheckpoint { get; init; }

    public TemplateSurveyReport Run(string rawDir, CancellationToken ct)
    {
        CssSelectors.Ensure(config.Extract.Content, "extract.content");
        var parser = new RawPageParser(config.Site, config.UrlPattern);
        var encoding = TextEncodings.Resolve(config.Site.Encoding);

        // 一個鏡像頁 = *.html(與 extract 的定義相同:.DS_Store、Thumbs.db 不是頁)
        var files = Directory.EnumerateFiles(rawDir, "*.html", SearchOption.AllDirectories)
            .Select(f => (File: f, Rel: Path.GetRelativePath(rawDir, f).Replace('\\', '/')))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();

        var clusters = new List<(HashSet<string> Skeleton, List<TemplatePage> Pages)>();
        foreach (var (file, rel) in files)
        {
            OnCheckpoint?.Invoke("page:" + rel);
            ct.ThrowIfCancellationRequested();
            var raw = parser.Parse(rawDir, file);
            var doc = _parser.ParseDocument(File.ReadAllText(file, encoding));
            var body = doc.Body;
            var skeleton = body is null ? [] : Skeleton(body);
            var lengths = new Dictionary<IElement, int>();
            var bodyChars = body is null ? 0 : Chars(body, lengths);
            // 選到的元素彼此巢狀時只算最外層,同一段字不重複計
            var matched = doc.QuerySelectorAll(config.Extract.Content).ToList();
            var contentChars = matched
                .Where(n => !matched.Any(o => o != n && o.Contains(n)))
                .Sum(n => lengths.TryGetValue(n, out var len) ? len : Chars(n, lengths));

            // 跟每一群的代表頁(第一頁)比;取最像的,不夠像就自成一群。頁依路徑排序,所以結果是決定性的
            var best = clusters
                .Select((c, i) => (Index: i, Similarity: Jaccard(c.Skeleton, skeleton)))
                .Where(x => x.Similarity >= SameTemplate)
                .OrderByDescending(x => x.Similarity).ThenBy(x => x.Index)
                .Select(x => (int?)x.Index)
                .FirstOrDefault();
            var page = new TemplatePage(rel, raw.Section, raw.Locale, 0, matched.Count, contentChars, bodyChars,
                body is null ? null : ContentCandidate(body, bodyChars, lengths));
            if (best is { } index)
            {
                clusters[index].Pages.Add(page);
            }
            else
            {
                clusters.Add((skeleton, [page]));
            }
        }

        var groups = clusters
            .OrderByDescending(c => c.Pages.Count).ThenBy(c => c.Pages[0].Page, StringComparer.Ordinal)
            .Select((c, i) => new TemplateGroup(i + 1, [.. c.Pages.Select(p => p with { Template = i + 1 })]))
            .ToList();
        return new TemplateSurveyReport { Groups = groups };
    }

    /// <summary>
    /// 寫兩份清單:<c>template_report.csv</c>(一個模板一列)與 <c>template_pages.csv</c>(一頁一列,方便查某一頁屬於哪一種)。
    /// </summary>
    public static void Write(string outDir, TemplateSurveyReport report)
    {
        Directory.CreateDirectory(outDir);
        var summary = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "template", "pages", "sections", "locales", "sample_pages",
                "content_empty", "content_coverage", "suggested_content", "suggested_pages",
            },
        };
        foreach (var g in report.Groups)
        {
            var suggested = g.Suggested;
            summary.Add(new[]
            {
                Name(g.Id), Num(g.Pages.Count), Counts(g.Pages.Select(p => p.Section.Length == 0 ? "(root)" : p.Section)),
                Counts(g.Pages.Select(p => p.Locale)), string.Join(" | ", g.Pages.Take(3).Select(p => p.Page)),
                Num(g.ContentEmpty), g.ContentCoverage is { } c ? Num(c) : "",
                suggested?.Selector ?? "", suggested is { } s ? Num(s.Pages) : "",
            });
        }
        Csv.Write(Path.Combine(outDir, "template_report.csv"), summary);

        var pages = new List<IReadOnlyList<string>>
        {
            new[] { "page", "template", "section", "lang", "content_matches", "content_chars", "body_chars", "content_candidate" },
        };
        foreach (var p in report.Groups.SelectMany(g => g.Pages).OrderBy(p => p.Page, StringComparer.Ordinal))
        {
            pages.Add(new[]
            {
                p.Page, Name(p.Template), p.Section, p.Locale, Num(p.ContentMatches), Num(p.ContentChars), Num(p.BodyChars), p.Candidate ?? "",
            });
        }
        Csv.Write(Path.Combine(outDir, "template_pages.csv"), pages);
    }

    public static string Name(int id) => "T" + id.ToString(CultureInfo.InvariantCulture);

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>news=12;events=3</c>:多的在前,同數依名稱。</summary>
    private static string Counts(IEnumerable<string> values) =>
        string.Join(';', values.GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={Num(g.Count())}"));

    /// <summary>body 底下前 <see cref="SkeletonDepth"/> 層版面元素的路徑集合。</summary>
    private static HashSet<string> Skeleton(IElement body)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        Walk(body, "", 0);
        return paths;

        void Walk(IElement parent, string prefix, int depth)
        {
            if (depth >= SkeletonDepth)
            {
                return;
            }
            foreach (var child in parent.Children)
            {
                var tag = child.LocalName;
                if (!LayoutTags.Contains(tag))
                {
                    continue;
                }
                var path = prefix + ">" + Token(child);
                paths.Add(path);
                Walk(child, path, depth + 1);
            }
        }
    }

    /// <summary><c>tag#id</c> 或 <c>tag.第一個class</c>,數字一律換成 <c>0</c>。</summary>
    private static string Token(IElement e)
    {
        var tag = e.LocalName;
        if (!string.IsNullOrEmpty(e.Id))
        {
            return tag + "#" + Digits().Replace(e.Id, "0");
        }
        var cls = e.ClassList.FirstOrDefault();
        return cls is null ? tag : tag + "." + Digits().Replace(cls, "0");
    }

    /// <summary>
    /// 內容區候選:有 id 或 class、而且字數占整頁至少 <see cref="ContentShare"/> 的元素裡最深的那一個——
    /// 越外層越容易把選單、頁尾一起包進來。回傳可以直接貼進 <c>extract.content</c> 的 selector;找不到是 null。
    /// </summary>
    private static string? ContentCandidate(IElement body, int bodyChars, Dictionary<IElement, int> lengths)
    {
        if (bodyChars == 0)
        {
            return null;
        }
        string? best = null;
        var bestDepth = -1;
        Visit(body, 0);
        return best;

        void Visit(IElement parent, int depth)
        {
            foreach (var child in parent.Children)
            {
                // 頁首頁尾選單不是內容區:圖片為主的頁,字數最多的反而是頁尾
                if (child.LocalName is "script" or "style" or "noscript" || IsChrome(child))
                {
                    continue;
                }
                var chars = lengths.GetValueOrDefault(child);
                if (chars < bodyChars * ContentShare)
                {
                    continue;   // 字數不夠的子樹,底下的元素只會更少
                }
                if (Selector(child) is { } selector && depth + 1 > bestDepth)
                {
                    best = selector;
                    bestDepth = depth + 1;
                }
                Visit(child, depth + 1);
            }
        }
    }

    /// <summary>可以直接用的 selector:<c>#id</c>,否則 <c>tag.class</c>;名字含特殊字元(要跳脫的)就不用。</summary>
    private static string? Selector(IElement e)
    {
        if (!string.IsNullOrEmpty(e.Id) && PlainName().IsMatch(e.Id))
        {
            return "#" + e.Id;
        }
        return e.ClassList.FirstOrDefault(c => PlainName().IsMatch(c)) is { } cls ? e.LocalName + "." + cls : null;
    }

    /// <summary>
    /// 元素的字數(每段文字空白正規化後的長度加總,不含 script / style / noscript),順手記下每個子元素的字數——
    /// 一頁只走一趟,找內容區候選時直接查表,不必每一層重算整棵子樹。
    /// </summary>
    private static int Chars(IElement e, Dictionary<IElement, int> lengths)
    {
        var total = 0;
        foreach (var node in e.ChildNodes)
        {
            total += node switch
            {
                IElement { LocalName: "script" or "style" or "noscript" } => 0,
                IElement child => Chars(child, lengths),
                IText text => Normalize(text.Data).Length,
                _ => 0,
            };
        }
        lengths[e] = total;
        return total;
    }

    private static string Normalize(string s) => Whitespace().Replace(s, " ").Trim();

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 1;
        }
        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    /// <summary>網站外框:header / footer / nav / aside,或 id、class 看得出是外框的(header、footer、nav、menu、sidebar、breadcrumb)。</summary>
    private static bool IsChrome(IElement e) =>
        e.LocalName is "header" or "footer" or "nav" or "aside"
        || (e.Id is { Length: > 0 } id && ChromeName().IsMatch(id))
        || e.ClassList.Any(c => ChromeName().IsMatch(c));

    [GeneratedRegex("header|footer|nav|menu|sidebar|breadcrumb", RegexOptions.IgnoreCase)]
    private static partial Regex ChromeName();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex PlainName();
}
