using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Extraction;

/// <summary>
/// 單頁結構化抽取(規格 main 迴圈):正文抽取、標題優先序、連結改寫、
/// 影片/PDF 佔位符保留(§2.6:markdownify/ReverseMarkdown 會丟 iframe/video)、
/// 圖片 → 根絕對網路路徑 + 壞圖偵測,最後 HTML → Markdown。
/// </summary>
public sealed partial class PageExtractor
{
    private const string VideoWrap =
        "<div style=\"position:relative;padding-bottom:56.25%;height:0;margin:18px 0;" +
        "border-radius:8px;overflow:hidden;background:#000;\">{0}</div>";

    private readonly SiteConfig _config;
    private readonly TitleCleaner _titles;
    private readonly LinkRewriter _links;
    private readonly PageClassifier _classifier;
    private readonly MediaPaths _mediaPaths;
    private readonly HtmlParser _parser = new();
    private readonly ReverseMarkdown.Converter _markdown = new(new ReverseMarkdown.Config
    {
        GithubFlavored = true,
        Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
        Formatting = { RemoveComments = true },
    });

    public PageExtractor(SiteConfig config)
    {
        _config = config;
        _titles = new TitleCleaner(config);
        _links = new LinkRewriter(config);
        _classifier = new PageClassifier(config);
        _mediaPaths = new MediaPaths(config);
    }

    public ExtractedPage Extract(RawPage page, string html, string mediaRoot)
    {
        var doc = _parser.ParseDocument(html);

        // 正文 = config selector 命中的節點;選不到退回 <body>(規格 extract_body)
        var nodes = doc.QuerySelectorAll(_config.Extract.Content).ToList();
        if (nodes.Count == 0 && doc.Body is { } body)
        {
            nodes = [body];
        }
        var textLength = string.Join(' ', nodes.Select(n => NormalizeWs(n.TextContent))).Trim().Length;
        var imageCount = nodes.Sum(n => n.QuerySelectorAll("img").Length);

        var pageType = _classifier.Classify(page.Section, page.Slug);
        var rules = _config.Extract.TypeRules.GetValueOrDefault(pageType);

        // 標題:此頁型的內文 selector 優先(原站 <title> 常帶日期前綴、甚至與內文不符)
        var title = "";
        if (rules?.TitleSelector is { } titleSelector
            && doc.QuerySelector(titleSelector) is { } el)
        {
            title = _titles.Clean(NormalizeWs(el.TextContent), stripSiteNoise: false);
        }
        if (title.Length == 0)
        {
            title = _titles.Clean(NormalizeWs(doc.QuerySelector("title")?.TextContent ?? ""), stripSiteNoise: true);
        }
        if (title.Length == 0)
        {
            title = page.Slug;
        }

        // 標題抽完才移除模板重複的日期/標題節點
        foreach (var selector in rules?.RemoveSelectors ?? [])
        {
            foreach (var e in doc.QuerySelectorAll(selector).ToArray())
            {
                e.Remove();
            }
        }

        var flags = new List<string>();
        if (_config.Extract.SectionFlags.TryGetValue(page.Section, out var sectionFlag))
        {
            flags.Add(sectionFlag);
        }
        if (textLength < _config.Extract.TextInImageMaxLength && imageCount >= 1 && pageType != PageClassifier.Listing)
        {
            flags.Add("text_in_image");
        }

        var embeds = new List<string>();
        var images = new List<ImageRef>();
        var videos = new List<Dictionary<string, string>>();
        var documents = new List<Dictionary<string, string>>();
        var mediaUses = new List<MediaUse>();
        var missing = new List<MissingImage>();
        var needFetch = new List<string>();
        var pdfLabel = _config.Media.PdfLabels.GetValueOrDefault(page.Locale, "View PDF");
        var combinedHtml = new StringBuilder();

        foreach (var node in nodes)
        {
            foreach (var a in node.QuerySelectorAll("a[href]"))
            {
                a.SetAttribute("href", _links.Rewrite(a.GetAttribute("href"), page.SourceUrl));
            }

            // 影片/PDF:轉 Markdown 會丟 iframe/video,改佔位符保留在原位置(含標題順序)
            foreach (var iframe in node.QuerySelectorAll("iframe[src]").ToArray())
            {
                var src = iframe.GetAttribute("src")!;
                var low = src.ToLowerInvariant().Split('?')[0];
                if (low.Contains("youtube.com/embed", StringComparison.Ordinal)
                    || low.Contains("youtu.be", StringComparison.Ordinal))
                {
                    var inner =
                        $"<iframe src=\"{src}\" title=\"video\" loading=\"lazy\" " +
                        "style=\"position:absolute;top:0;left:0;width:100%;height:100%;border:0;\" " +
                        "allow=\"accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture\" " +
                        "allowfullscreen></iframe>";
                    embeds.Add(string.Format(VideoWrap, inner));
                    videos.Add(new() { ["type"] = "youtube", ["url"] = src });
                    ReplaceWithPlaceholder(iframe, embeds.Count - 1);
                }
                else if (low.EndsWith(".pdf", StringComparison.Ordinal)
                         && TryResolve(page.SourceUrl, src) is { } absPdf
                         && MediaPaths.RelativeFromUrl(absPdf) is { } pdfRel)
                {
                    var web = _mediaPaths.WebPath(pdfRel);
                    // 內嵌預覽(比照原版 iframe),下方附下載連結備用
                    embeds.Add(
                        "<div style=\"margin:18px 0;text-align:center;\">" +
                        $"<iframe src=\"{web}\" title=\"PDF\" loading=\"lazy\" " +
                        "style=\"width:100%;height:820px;border:1px solid #e5ddc8;border-radius:8px;\"></iframe>" +
                        $"<p style=\"font-size:15px;margin:8px 0;\"><a href=\"{web}\" target=\"_blank\" " +
                        $"rel=\"noopener\" style=\"color:#b56f13;\">📄 {pdfLabel}</a></p></div>");
                    documents.Add(new() { ["src"] = web, ["orig"] = absPdf });
                    needFetch.Add(absPdf);
                    ReplaceWithPlaceholder(iframe, embeds.Count - 1);
                }
                // 其他 iframe(如 Google 行事曆)保留原樣
            }

            foreach (var video in node.QuerySelectorAll("video").ToArray())
            {
                var inner = "";
                foreach (var source in video.QuerySelectorAll("source[src]"))
                {
                    if (TryResolve(page.SourceUrl, source.GetAttribute("src")!) is not { } absVid
                        || MediaPaths.RelativeFromUrl(absVid) is not { } vidRel)
                    {
                        continue;
                    }
                    var web = _mediaPaths.WebPath(vidRel);
                    inner = $"<video controls preload=\"metadata\" src=\"{web}\" " +
                            "style=\"position:absolute;top:0;left:0;width:100%;height:100%;background:#000;\"></video>";
                    videos.Add(new() { ["type"] = "local", ["src"] = web });
                    needFetch.Add(absVid);
                }
                if (inner.Length > 0)
                {
                    embeds.Add(string.Format(VideoWrap, inner));
                    ReplaceWithPlaceholder(video, embeds.Count - 1);
                }
                else
                {
                    video.Remove();
                }
            }

            foreach (var img in node.QuerySelectorAll("img[src]").ToArray())
            {
                if (TryResolve(page.SourceUrl, img.GetAttribute("src")!) is not { } absUrl
                    || MediaPaths.RelativeFromUrl(absUrl) is not { } rel)
                {
                    continue;
                }
                var web = _mediaPaths.WebPath(rel);
                var alt = img.GetAttribute("alt") ?? "";
                if (File.Exists(MediaPaths.LocalPath(mediaRoot, rel)))
                {
                    images.Add(new ImageRef(web, alt));   // 只有真的存在才進相簿
                    mediaUses.Add(new MediaUse(rel, absUrl, page.SourceUrl, alt));
                }
                else
                {
                    missing.Add(new MissingImage(page.SourceUrl, web));   // 原站壞圖(404),記錄不入相簿
                }
                // 相簿頁型:內文移除圖片避免與 frontmatter images 重複;其他頁型保留內文圖
                if (rules?.ImagesToGallery == true)
                {
                    ((img.ParentElement?.TagName == "A" ? img.ParentElement : img) as IElement)!.Remove();
                }
                else
                {
                    img.SetAttribute("src", web);
                }
            }

            foreach (var junk in node.QuerySelectorAll("script, style").ToArray())
            {
                junk.Remove();
            }
            combinedHtml.Append(node.OuterHtml);
        }

        var bodyMarkdown = CleanupMarkdown(_markdown.Convert(combinedHtml.ToString()));
        // 還原影片/PDF 佔位符為內嵌 HTML(保留在原位置)
        for (var i = 0; i < embeds.Count; i++)
        {
            bodyMarkdown = bodyMarkdown.Replace($"@@EMBED{i}@@", "\n\n" + embeds[i] + "\n\n");
        }
        while (bodyMarkdown.Contains("\n\n\n", StringComparison.Ordinal))
        {
            bodyMarkdown = bodyMarkdown.Replace("\n\n\n", "\n\n");
        }
        bodyMarkdown = bodyMarkdown.Trim();

        return new ExtractedPage
        {
            Page = page,
            Title = title,
            PageType = pageType,
            Flags = flags,
            TextLength = textLength,
            ImageCount = imageCount,
            BodyMarkdown = bodyMarkdown,
            Images = images,
            Videos = videos,
            Documents = documents,
            MediaUses = mediaUses,
            MissingImages = missing,
            NeedFetch = needFetch,
        };
    }

    /// <summary>
    /// 與規格(markdownify)行為對齊的輸出清理:ReverseMarkdown 會留下
    /// 空清單項(相簿圖移除後的空 &lt;li&gt;)、無文字連結(輪播控制鈕)與全空白行,markdownify 則略過。
    /// </summary>
    private static string CleanupMarkdown(string markdown)
    {
        markdown = EmptyLink().Replace(markdown, "");   // [](x);(?<!!) 保住空 alt 的圖片 ![](x)
        var lines = markdown.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !EmptyListItem().IsMatch(l));
        return string.Join('\n', lines).Trim();
    }

    private static void ReplaceWithPlaceholder(IElement element, int index) =>
        element.ReplaceWith(element.Owner!.CreateTextNode($"@@EMBED{index}@@"));

    private static string? TryResolve(string baseUrl, string relative)
    {
        try
        {
            return new Uri(new Uri(baseUrl), relative).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string NormalizeWs(string s) => WhitespaceRun().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"(?<!\!)\[\]\([^)]*\)")]
    private static partial Regex EmptyLink();

    [GeneratedRegex(@"^\s*(\d+\.|[-*+])\s*$")]
    private static partial Regex EmptyListItem();
}
