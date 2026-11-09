using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Inventory;
using PolyMigrate.Core.Templates;

namespace PolyMigrate.Core.Tests.Templates;

/// <summary>
/// 模板分群:寫 config 之前先知道舊站有幾種版面、<c>extract.content</c> 在每一種上抓不抓得到東西。
/// </summary>
public class TemplateSurveyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-templates").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Raw => Path.Combine(_root, "raw");

    private static SiteConfig Config(string content = "#content") => new()
    {
        Site = new SiteSection { BaseUrl = "https://old.example.org" },
        UrlPattern = new UrlPatternSection
        {
            LangMap = new Dictionary<string, string> { ["ch"] = "zh-Hant", ["en"] = "en" },
            DefaultLang = "zh-Hant",
            StripExtensions = [".php"],
        },
        Extract = new ExtractSection { Content = content },
    };

    private void Add(string relative, string body)
    {
        var path = Path.Combine(Raw, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"<html><head><title>t</title></head><body>{body}</body></html>");
    }

    private const string Menu = "<div id=\"header\"><ul class=\"menu\"><li>Home</li><li>News</li><li>About</li></ul></div>";
    private const string Footer = "<div id=\"footer\">Copyright 2026 Example Temple, all rights reserved. Address and phone.</div>";

    private static string Article(int n, string text) =>
        Menu + $"<div id=\"wrap\"><div id=\"content\" class=\"post-{n}\"><h1>Title {n}</h1><p>{text}</p></div></div>" + Footer;

    private static string Listing(string items) =>
        Menu + $"<div id=\"wrap\"><div class=\"list\"><ul class=\"items\">{items}</ul></div></div>" + Footer;

    private TemplateSurveyReport Run(SiteConfig? config = null) => new TemplateSurvey(config ?? Config()).Run(Raw, CancellationToken.None);

    [Fact]
    public void PagesWithTheSameSkeleton_AreOneTemplate_EvenWithDifferentIdsDigitsAndText()
    {
        Add("ch/news/1.php.html", Article(1, new string('甲', 300)));
        Add("ch/news/2.php.html", Article(2, new string('乙', 500)));
        Add("en/news/3.php.html", Article(3, new string('x', 400)));
        Add("ch/news/index.php.html", Listing("<li>one</li><li>two</li>"));

        var report = Run();

        Assert.Equal(2, report.Groups.Count);
        var articles = report.Groups[0];   // 頁數多的在前
        Assert.Equal(1, articles.Id);
        Assert.Equal(["ch/news/1.php.html", "ch/news/2.php.html", "en/news/3.php.html"], articles.Pages.Select(p => p.Page));
        Assert.Equal(("#content", 3), articles.Suggested);
        Assert.Equal(0, articles.ContentEmpty);
        Assert.True(articles.ContentCoverage > 80);
    }

    [Fact]
    public void ATemplateWhereExtractContentSelectsNothing_IsCounted_AndGetsASuggestion()
    {
        Add("ch/news/1.php.html", Article(1, new string('甲', 300)));
        Add("ch/news/index.php.html", Listing(string.Concat(Enumerable.Range(1, 30).Select(i => $"<li>item number {i}</li>"))));

        var report = Run();

        var listing = report.Groups.Single(g => g.Pages[0].Page == "ch/news/index.php.html");
        Assert.Equal(1, listing.ContentEmpty);
        Assert.Equal(0, listing.ContentCoverage);
        Assert.Equal("ul.items", listing.Suggested?.Selector);   // 最深、字數又夠多的那一層;外層的 div.list 也夠多但比較淺
        Assert.Equal(1, report.ContentEmpty);
    }

    [Fact]
    public void AnImageOnlyPage_IsNotEmpty_AndTheFooterIsNeverSuggested()
    {
        // 活動海報頁:內容區只有一張圖,字數最多的是頁尾。內容區選到了就不算「抓不到」;頁尾、選單不是內容區
        Add("ch/events/poster.php.html", Menu + "<div id=\"content\"><img src=\"poster.jpg\" alt=\"\"></div>" + Footer);

        var page = Run().Groups.Single().Pages.Single();

        Assert.Equal(1, page.ContentMatches);
        Assert.Equal(0, page.ContentChars);
        Assert.Null(page.Candidate);
    }

    [Fact]
    public void Write_ProducesTheReportAndThePerPageList()
    {
        Add("ch/news/1.php.html", Article(1, "abc"));
        Add("ch/index.php.html", Menu + "<div id=\"home\"><p>" + new string('w', 200) + "</p></div>" + Footer);

        TemplateSurvey.Write(_root, Run());

        var report = Csv.ReadRows(Path.Combine(_root, "template_report.csv")).ToList();
        Assert.Equal(
            ["template", "pages", "sections", "locales", "sample_pages", "content_empty", "content_coverage", "suggested_content", "suggested_pages"],
            report[0]);
        Assert.Equal(3, report.Count);
        Assert.Contains(report, r => r[2] == "(root)=1" && r[5] == "1" && r[7] == "#home");   // 根層頁沒有 section
        var pages = Csv.ReadRows(Path.Combine(_root, "template_pages.csv")).ToList();
        Assert.Equal(["page", "template", "section", "lang", "content_matches", "content_chars", "body_chars", "content_candidate"], pages[0]);
        Assert.Equal(["ch/index.php.html", "ch/news/1.php.html"], pages.Skip(1).Select(r => r[0]));
    }

    [Fact]
    public void JunkFilesInTheMirror_AreNotPages()
    {
        Add("ch/news/1.php.html", Article(1, "abc"));
        File.WriteAllText(Path.Combine(Raw, "ch", "news", "Thumbs.db"), "junk");

        Assert.Equal(1, Run().PagesSurveyed);
    }

    [Fact]
    public void Cancellation_IsCheckedOnEveryPage()
    {
        Add("ch/news/1.php.html", Article(1, "a"));
        Add("ch/news/2.php.html", Article(2, "b"));
        using var cts = new CancellationTokenSource();
        var seen = new List<string>();
        var survey = new TemplateSurvey(Config())
        {
            OnCheckpoint = label =>
            {
                seen.Add(label);
                if (seen.Count == 2)
                {
                    cts.Cancel();
                }
            },
        };

        Assert.Throws<OperationCanceledException>(() => survey.Run(Raw, cts.Token));
        Assert.Equal(["page:ch/news/1.php.html", "page:ch/news/2.php.html"], seen);
    }
}
