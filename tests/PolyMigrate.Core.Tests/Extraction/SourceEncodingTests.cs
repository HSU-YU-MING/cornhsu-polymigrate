using System.Text;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>
/// 來源站編碼(§3.1):鏡像存的是原始 bytes,抽取時才依 config 的 site.encoding 解碼,
/// 輸出一律 UTF-8。fixture 站目前全是 UTF-8(其 README 的 Big5 項仍未勾),所以這條路徑原本沒有測試。
/// </summary>
public class SourceEncodingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("polymigrate-encoding").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Big5Mirror_DecodedPerConfig_WrittenAsUtf8()
    {
        var config = SiteConfigLoader.Load(
            """
            config_version: 1
            site:
              base_url: https://old.example.org
              encoding: big5
            url_pattern:
              lang_map: { ch: zh-Hant }
              default_lang: zh-Hant
            extract:
              content: main
            """);
        var big5 = TextEncodings.Resolve("big5");
        var raw = Path.Combine(_root, "raw", "ch", "news");
        Directory.CreateDirectory(raw);
        File.WriteAllBytes(Path.Combine(raw, "a.php.html"),
            big5.GetBytes("<html><head><title>浴佛法會</title></head><body><main><p>歡迎大眾參加</p></main></body></html>"));

        new ExtractionPipeline(config).Run(
            new ExtractionPaths(Path.Combine(_root, "raw"), Path.Combine(_root, "media"), _root));

        var md = File.ReadAllText(Path.Combine(_root, "content", "ch", "news", "a.md"), new UTF8Encoding(false, true));
        Assert.Contains("title: 浴佛法會", md);
        Assert.Contains("歡迎大眾參加", md);
    }
}
