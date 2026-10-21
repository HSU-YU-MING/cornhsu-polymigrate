using System.Text;
using PolyMigrate.Core.Configuration;
using PolyMigrate.Core.Extraction;
using PolyMigrate.Core.Verify;

namespace PolyMigrate.Core.Tests.Extraction;

/// <summary>
/// 輸出檔的位元組格式契約(contracts.md「通用規則」)。
///
/// <para>GoldenTests 比的是「內容」:它用 ReadAllText 讀(會吃掉 BOM)、再刪掉所有 \r——
/// 所以 CSV 的 BOM 與 CRLF 掉了、Markdown 多長出一個 BOM,golden 照樣全綠。
/// 這兩件事都是契約(CSV 走 Excel 人工覆核,無 BOM 中文會亂碼;Markdown 給靜態站產生器吃,
/// 開頭多一個 BOM 會讓 frontmatter 圍欄認不出來),必須逐位元組看。</para>
/// </summary>
public class OutputBytesTests : IDisposable
{
    private static readonly string FixtureDir = Path.Combine(FindRepoRoot(), "tests", "fixtures", "site");
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _out = Directory.CreateTempSubdirectory("polymigrate-bytes").FullName;

    public void Dispose() => Directory.Delete(_out, recursive: true);

    private void RunFixture()
    {
        var config = SiteConfigLoader.LoadFile(Path.Combine(FixtureDir, "config.yaml"));
        new ExtractionPipeline(config).Run(new ExtractionPaths(
            Path.Combine(FixtureDir, "raw"), Path.Combine(FixtureDir, "media"), _out));
    }

    /// <summary>UTF-8 含 BOM、每個 \n 前面都是 \r、沒有落單的 \r、最後一列也以 \r\n 結尾。</summary>
    private static void AssertCsvBytes(string path)
    {
        var name = Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.AsSpan().StartsWith(Utf8Bom), $"{name}:缺 UTF-8 BOM(Excel 開中文會亂碼)");
        Assert.True(bytes.AsSpan().EndsWith("\r\n"u8), $"{name}:最後一列沒有以 CRLF 結尾");
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                Assert.True(i > 0 && bytes[i - 1] == (byte)'\r', $"{name}:位移 {i} 有落單的 LF");
            }
            if (bytes[i] == (byte)'\r')
            {
                Assert.True(i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n', $"{name}:位移 {i} 有落單的 CR");
            }
        }
    }

    [Fact]
    public void EveryCsvDeliverable_IsUtf8WithBom_AndCrlf()
    {
        RunFixture();

        // 逐一檢查實際寫出的每一份,而不是只看寫死的清單:日後新增一份 CSV 也自動受這條契約約束
        var csvFiles = Directory.GetFiles(_out, "*.csv", SearchOption.TopDirectoryOnly);
        var names = csvFiles.Select(Path.GetFileName).ToList();
        foreach (var expected in (string[])["content_inventory.csv", "hreflang_map.csv", "media_manifest.csv",
                     "missing_images.csv", "path_issues.csv", "redirect_map.csv"])
        {
            Assert.Contains(expected, names);
        }
        foreach (var csv in csvFiles)
        {
            AssertCsvBytes(csv);
        }
    }

    [Fact]
    public void VerifyReportCsv_IsUtf8WithBom_AndCrlf()
    {
        // verify_report.csv 不在 golden 裡(GoldenTests 刻意排除),所以它的格式沒有任何別的網
        RunFixture();

        new OutputVerifier().Run(_out, Path.Combine(FixtureDir, "media"));

        AssertCsvBytes(Path.Combine(_out, "verify_report.csv"));
    }

    [Fact]
    public void Markdown_HasNoBom_FencedBody_AndSingleTrailingNewline()
    {
        RunFixture();

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var files = Directory.GetFiles(Path.Combine(_out, "content"), "*.md", SearchOption.AllDirectories);
        Assert.Equal(13, files.Length);
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(_out, file);
            var bytes = File.ReadAllBytes(file);

            // 第一個位元組就必須是圍欄:前面多一個 BOM,Hugo/Eleventy 等就認不出 frontmatter
            Assert.True(bytes.AsSpan().StartsWith("---\n"u8), $"{name}:開頭不是 '---\\n'(有 BOM 或行尾不對)");
            var text = strictUtf8.GetString(bytes);

            var close = text.IndexOf("\n---\n\n", 3, StringComparison.Ordinal);
            Assert.True(close > 0, $"{name}:找不到結束圍欄 '---' 與其後的空行");
            var body = text[(close + "\n---\n\n".Length)..];

            // 內文由 CleanupMarkdown 逐行 TrimEnd,一律 LF;結尾恰好一個換行
            Assert.DoesNotContain('\r', body);
            Assert.EndsWith("\n", body);
            Assert.False(body.EndsWith("\n\n", StringComparison.Ordinal), $"{name}:結尾多了空行");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "PolyMigrate.slnx")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("repo root not found");
        }
        return dir;
    }
}
