using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>
/// 第三輪審查的原則:**頁的路由必須與 9d17071 逐位元相同**——redirect_map、nginx、_redirects、
/// 內文裡指向鏡像頁的連結,對任何輸入都不能變。9254591 曾把「剝完 strip_extensions 還帶副檔名」的頁
/// 判成檔案、拿掉尾斜線(/jane.doe.php → /jane.doe、/CONTACT.PHP 與 /calendar.html 的 301 消失、
/// key 為 zh.tw 的語言首頁變 /zh.tw)。這裡用審查的三個測試站(review3 的 site-r3、site-r3lang、
/// site-r3clean)跑完整管線,期望值是 9d17071 實跑的輸出,逐列比對。
/// </summary>
public class PageRouteStabilityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-routes").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Run(string configYaml, (string Path, string Html)[] raw)
    {
        foreach (var (path, html) in raw)
        {
            var file = Path.Combine(_root, "raw", path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, html);
        }
        new ExtractionPipeline(SiteConfigLoader.Load(configYaml)).Run(ExtractionPaths.ForRoot(_root));
        return _root;
    }

    private string[] Lines(string relative) =>
        File.ReadAllText(Path.Combine(_root, relative)).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);   // ReadAllText 已去掉 CSV 的 BOM

    [Fact]
    public void RootLevelPagesWithDots_KeepThe9d17071Routes()
    {
        Run(R3Config, R3Raw);

        Assert.Equal(R3RedirectMap, Lines("redirect_map.csv"));
        Assert.Equal(R3Netlify, Lines("_redirects"));
        Assert.Equal(R3Nginx, Lines("redirects.nginx.conf"));
        var hub = Lines(Path.Combine("content", "ch", "index.md"));
        foreach (var link in R3HubMirrorLinks)
        {
            Assert.Contains(link, hub);   // 指向鏡像頁的連結:與 9d17071 相同
        }
    }

    [Fact]
    public void LanguageKeyWithADot_KeepsItsHomeRoute()
    {
        Run(R3LangConfig, R3LangRaw);

        Assert.Equal(R3LangRedirectMap, Lines("redirect_map.csv"));
        Assert.Contains("body text. [home](/zh.tw/) [home2](/zh.tw/)", Lines(Path.Combine("content", "zh.tw", "news", "a.md")));
    }

    [Fact]
    public void CleanUrlSiteWithDots_KeepsThe9d17071Routes()
    {
        Run(R3CleanConfig, R3CleanRaw);

        Assert.Equal(R3CleanRedirectMap, Lines("redirect_map.csv"));
        var home = Lines(Path.Combine("content", "index.md"));
        Assert.Contains(home, l => l.Contains("[jd](/jane.doe/)") && l.Contains("[nj](/node.js/)") && l.Contains("[ab](/about/)"));
    }

    private const string R3Config =
        """
        config_version: 1
        site:
          base_url: https://legacy.example.org
        url_pattern:
          lang_map: { ch: zh-Hant, en: en }
          default_lang: zh-Hant
          strip_extensions: [.php]
        extract:
          content: "main"
        """;

    private const string R3LangConfig =
        """
        config_version: 1
        site:
          base_url: https://legacy.example.org
        url_pattern:
          lang_map: { zh.tw: zh-Hant, en: en }
          default_lang: zh-Hant
          strip_extensions: [.php]
        extract:
          content: "main"
        """;

    private const string R3CleanConfig =
        """
        config_version: 1
        site:
          base_url: https://legacy.example.org
        url_pattern:
          lang_map: { "": en }
          default_lang: en
          strip_extensions: []
        extract:
          content: "main"
        """;

    private static readonly (string Path, string Html)[] R3Raw =
    [
        ("2019.05.01.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>2019.05.01.php</title></head><body><main><h1>2019.05.01.php</h1><p>body text for 2019.05.01.php page.</p></main></body></html>"),
        ("2026.q1.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>2026.q1.php</title></head><body><main><h1>2026.q1.php</h1><p>body text for 2026.q1.php page.</p></main></body></html>"),
        ("CONTACT.PHP.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>CONTACT.PHP</title></head><body><main><h1>CONTACT.PHP</h1><p>body text for CONTACT.PHP page.</p></main></body></html>"),
        ("about.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>about.php</title></head><body><main><h1>about.php</h1><p>body text for about.php page.</p></main></body></html>"),
        ("asp.net.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>asp.net.php</title></head><body><main><h1>asp.net.php</h1><p>body text for asp.net.php page.</p></main></body></html>"),
        ("calendar.html.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>calendar.html</title></head><body><main><h1>calendar.html</h1><p>body text for calendar.html page.</p></main></body></html>"),
        ("ch/index.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>hub</title></head><body><main><h1>hub</h1><ul><li><a href=\"/about.php\">L /about.php</a></li><li><a href=\"/jane.doe.php\">L /jane.doe.php</a></li><li><a href=\"/node.js.php\">L /node.js.php</a></li><li><a href=\"/asp.net.php\">L /asp.net.php</a></li><li><a href=\"/v2.0.release.php\">L /v2.0.release.php</a></li><li><a href=\"/index.en.php\">L /index.en.php</a></li><li><a href=\"/2026.q1.php\">L /2026.q1.php</a></li><li><a href=\"/CONTACT.PHP\">L /ABOUT.PHP</a></li><li><a href=\"/禪修.v2.php\">L /禪修.v2.php</a></li><li><a href=\"/關於.中文.php\">L /關於.中文.php</a></li><li><a href=\"/calendar.html\">L /calendar.html</a></li><li><a href=\"/v1.2-release.php\">L /v1.2-release.php</a></li><li><a href=\"/2019.05.01.php\">L /2019.05.01.php</a></li><li><a href=\"/john.smith\">L /john.smith</a></li><li><a href=\"/brochure.pdf\">L /brochure.pdf</a></li><li><a href=\"/embed.swf\">L /embed.swf</a></li><li><a href=\"/ch/files/x.pdf\">L /ch/files/x.pdf</a></li><li><a href=\"../jane.doe.php\">L ../jane.doe.php</a></li><li><a href=\"mailto:a@legacy.example.org\">L mailto:a@legacy.example.org</a></li><li><a href=\"tel:+15125551234\">L tel:+15125551234</a></li><li><a href=\"javascript:void(0)\">L javascript:void(0)</a></li><li><a href=\"data:text/html,hi\">L data:text/html,hi</a></li><li><a href=\"ftp://legacy.example.org/ch/file.php\">L ftp://legacy.example.org/ch/file.php</a></li><li><a href=\"FTP://LEGACY.EXAMPLE.ORG/ch/file.php\">L FTP://LEGACY.EXAMPLE.ORG/ch/file.php</a></li><li><a href=\"file://legacy.example.org/ch/x.php\">L file://legacy.example.org/ch/x.php</a></li><li><a href=\"//legacy.example.org/ch/x.php\">L //legacy.example.org/ch/x.php</a></li><li><a href=\"ws://legacy.example.org/ch/x.php\">L ws://legacy.example.org/ch/x.php</a></li><li><a href=\"sms:+1555\">L sms:+1555</a></li><li><a href=\"https://legacy.example.org/about.php\">L https://legacy.example.org/about.php</a></li><li><a href=\"about:blank\">L about:blank</a></li><li><a href=\" javascript:alert(1)\">L  javascript:alert(1)</a></li></ul><iframe src=\"/about.php\" width=\"10\"></iframe><iframe src=\"/jane.doe.php\" width=\"10\"></iframe><iframe src=\"/calendar.html\" width=\"10\"></iframe><iframe src=\"/embed.swf\" width=\"10\"></iframe><iframe src=\"/brochure.html\" width=\"10\"></iframe><iframe src=\"ftp://legacy.example.org/ch/file.php\" width=\"10\"></iframe><iframe src=\"file://legacy.example.org/ch/x.php\" width=\"10\"></iframe><iframe src=\"//legacy.example.org/ch/x.php\" width=\"10\"></iframe><iframe src=\"javascript:void(0)\" width=\"10\"></iframe><iframe src=\"data:text/html,hi\" width=\"10\"></iframe><iframe src=\"about:blank\" width=\"10\"></iframe><iframe src=\"ws://legacy.example.org/ch/x.php\" width=\"10\"></iframe><iframe src=\"mailto:a@legacy.example.org\" width=\"10\"></iframe></main></body></html>"),
        ("index.en.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>index.en.php</title></head><body><main><h1>index.en.php</h1><p>body text for index.en.php page.</p></main></body></html>"),
        ("jane.doe.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>jane.doe.php</title></head><body><main><h1>jane.doe.php</h1><p>body text for jane.doe.php page.</p></main></body></html>"),
        ("john.smith.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>john.smith</title></head><body><main><h1>john.smith</h1><p>body text for john.smith page.</p></main></body></html>"),
        ("node.js.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>node.js.php</title></head><body><main><h1>node.js.php</h1><p>body text for node.js.php page.</p></main></body></html>"),
        ("v1.2-release.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>v1.2-release.php</title></head><body><main><h1>v1.2-release.php</h1><p>body text for v1.2-release.php page.</p></main></body></html>"),
        ("v2.0.release.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>v2.0.release.php</title></head><body><main><h1>v2.0.release.php</h1><p>body text for v2.0.release.php page.</p></main></body></html>"),
        ("禪修.v2.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>禪修.v2.php</title></head><body><main><h1>禪修.v2.php</h1><p>body text for 禪修.v2.php page.</p></main></body></html>"),
        ("關於.中文.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>關於.中文.php</title></head><body><main><h1>關於.中文.php</h1><p>body text for 關於.中文.php page.</p></main></body></html>"),
    ];

    private static readonly (string Path, string Html)[] R3LangRaw =
    [
        ("en/index.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>enhome</title></head><body><main><h1>enhome</h1><p>body text. <a href=\"/zh.tw/index.php\">zh</a></p></main></body></html>"),
        ("zh.tw/index.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>home</title></head><body><main><h1>home</h1><p>body text. <a href=\"/zh.tw/news/a.php\">a</a> <a href=\"/en/index.php\">en</a></p></main></body></html>"),
        ("zh.tw/news/a.php.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>a</title></head><body><main><h1>a</h1><p>body text. <a href=\"/zh.tw/index.php\">home</a> <a href=\"../index.php\">home2</a></p></main></body></html>"),
    ];

    private static readonly (string Path, string Html)[] R3CleanRaw =
    [
        ("about.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>ab</title></head><body><main><h1>ab</h1><p>body text. <a href=\"/\">home</a></p></main></body></html>"),
        ("index.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>home</title></head><body><main><h1>home</h1><p>body text. <a href=\"/jane.doe\">jd</a> <a href=\"/about\">ab</a> <a href=\"/node.js\">nj</a> <a href=\"/news/x.y\">xy</a> <a href=\"/files/b.pdf\">pdf</a> <a href=\"/b.pdf\">rootpdf</a></p></main></body></html>"),
        ("jane.doe.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>jd</title></head><body><main><h1>jd</h1><p>body text. <a href=\"/\">home</a></p></main></body></html>"),
        ("news/x.y.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>xy</title></head><body><main><h1>xy</h1><p>body text. <a href=\"/\">home</a></p></main></body></html>"),
        ("node.js.html", "<!doctype html><html><head><meta charset=\"utf-8\"><title>nj</title></head><body><main><h1>nj</h1><p>body text. <a href=\"/\">home</a></p></main></body></html>"),
    ];

    private static readonly string[] R3RedirectMap =
    [
        "old_url,new_path,lang,translation_key",
        "https://legacy.example.org/2019.05.01.php,/2019.05.01/,zh-Hant,/2019.05.01",
        "https://legacy.example.org/2026.q1.php,/2026.q1/,zh-Hant,/2026.q1",
        "https://legacy.example.org/CONTACT.PHP,/CONTACT.PHP/,zh-Hant,/CONTACT.PHP",
        "https://legacy.example.org/about.php,/about/,zh-Hant,/about",
        "https://legacy.example.org/asp.net.php,/asp.net/,zh-Hant,/asp.net",
        "https://legacy.example.org/calendar.html,/calendar.html/,zh-Hant,/calendar.html",
        "https://legacy.example.org/ch/index.php,/ch/,zh-Hant,index",
        "https://legacy.example.org/index.en.php,/index.en/,zh-Hant,/index.en",
        "https://legacy.example.org/jane.doe.php,/jane.doe/,zh-Hant,/jane.doe",
        "https://legacy.example.org/john.smith,/john.smith/,zh-Hant,/john.smith",
        "https://legacy.example.org/node.js.php,/node.js/,zh-Hant,/node.js",
        "https://legacy.example.org/v1.2-release.php,/v1.2-release/,zh-Hant,/v1.2-release",
        "https://legacy.example.org/v2.0.release.php,/v2.0.release/,zh-Hant,/v2.0.release",
        "https://legacy.example.org/禪修.v2.php,/%E7%A6%AA%E4%BF%AE.v2/,zh-Hant,/禪修.v2",
        "https://legacy.example.org/關於.中文.php,/%E9%97%9C%E6%96%BC.%E4%B8%AD%E6%96%87/,zh-Hant,/關於.中文",
    ];

    private static readonly string[] R3Netlify =
    [
        "/%E7%A6%AA%E4%BF%AE.v2.php /%E7%A6%AA%E4%BF%AE.v2/ 301",
        "/%E9%97%9C%E6%96%BC.%E4%B8%AD%E6%96%87.php /%E9%97%9C%E6%96%BC.%E4%B8%AD%E6%96%87/ 301",
        "/2019.05.01.php /2019.05.01/ 301",
        "/2026.q1.php /2026.q1/ 301",
        "/CONTACT.PHP /CONTACT.PHP/ 301",
        "/about.php /about/ 301",
        "/asp.net.php /asp.net/ 301",
        "/calendar.html /calendar.html/ 301",
        "/ch/index.php /ch/ 301",
        "/index.en.php /index.en/ 301",
        "/jane.doe.php /jane.doe/ 301",
        "/john.smith /john.smith/ 301",
        "/node.js.php /node.js/ 301",
        "/v1.2-release.php /v1.2-release/ 301",
        "/v2.0.release.php /v2.0.release/ 301",
    ];

    private static readonly string[] R3Nginx =
    [
        "location = /2019.05.01.php { return 301 /2019.05.01/; }",
        "location = /2026.q1.php { return 301 /2026.q1/; }",
        "location = /CONTACT.PHP { return 301 /CONTACT.PHP/; }",
        "location = /about.php { return 301 /about/; }",
        "location = /asp.net.php { return 301 /asp.net/; }",
        "location = /calendar.html { return 301 /calendar.html/; }",
        "location = /ch/index.php { return 301 /ch/; }",
        "location = /index.en.php { return 301 /index.en/; }",
        "location = /jane.doe.php { return 301 /jane.doe/; }",
        "location = /john.smith { return 301 /john.smith/; }",
        "location = /node.js.php { return 301 /node.js/; }",
        "location = /v1.2-release.php { return 301 /v1.2-release/; }",
        "location = /v2.0.release.php { return 301 /v2.0.release/; }",
        "location = /禪修.v2.php { return 301 /%E7%A6%AA%E4%BF%AE.v2/; }",
        "location = /關於.中文.php { return 301 /%E9%97%9C%E6%96%BC.%E4%B8%AD%E6%96%87/; }",
    ];

    private static readonly string[] R3LangRedirectMap =
    [
        "old_url,new_path,lang,translation_key",
        "https://legacy.example.org/en/index.php,/en/,en,index",
        "https://legacy.example.org/zh.tw/index.php,/zh.tw/,zh-Hant,index",
        "https://legacy.example.org/zh.tw/news/a.php,/zh.tw/news/a,zh-Hant,news/a",
    ];

    private static readonly string[] R3CleanRedirectMap =
    [
        "old_url,new_path,lang,translation_key",
        "https://legacy.example.org/about,/about/,en,/about",
        "https://legacy.example.org/index,/,en,/index",
        "https://legacy.example.org/jane.doe,/jane.doe/,en,/jane.doe",
        "https://legacy.example.org/news/x.y,/news/x.y,en,/news/x.y",
        "https://legacy.example.org/node.js,/node.js/,en,/node.js",
    ];

    private static readonly string[] R3HubMirrorLinks =
    [
        "- [L /about.php](/about/)",
        "- [L /jane.doe.php](/jane.doe/)",
        "- [L /node.js.php](/node.js/)",
        "- [L /asp.net.php](/asp.net/)",
        "- [L /v2.0.release.php](/v2.0.release/)",
        "- [L /index.en.php](/index.en/)",
        "- [L /2026.q1.php](/2026.q1/)",
        "- [L /ABOUT.PHP](/CONTACT.PHP/)",
        "- [L /禪修.v2.php](/%E7%A6%AA%E4%BF%AE.v2/)",
        "- [L /關於.中文.php](/%E9%97%9C%E6%96%BC.%E4%B8%AD%E6%96%87/)",
        "- [L /calendar.html](/calendar.html/)",
        "- [L /v1.2-release.php](/v1.2-release/)",
        "- [L /2019.05.01.php](/2019.05.01/)",
        "- [L /john.smith](/john.smith/)",
        "- [L ../jane.doe.php](/jane.doe/)",
        "- [L https://legacy.example.org/about.php](https://legacy.example.org/about.php)",
    ];
}
