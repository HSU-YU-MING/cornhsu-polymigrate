using PolyMigrate.Core.Markdown;
using YamlDotNet.Serialization;

namespace PolyMigrate.Core.Tests.Markdown;

public class FrontmatterSerializerTests
{
    private static string Serialize(params (string Key, object? Value)[] fields) =>
        FrontmatterSerializer.ToBlock(fields.ToDictionary(f => f.Key, f => f.Value));

    private static Dictionary<string, object> Roundtrip(string block)
    {
        var yaml = block.Split("---\n")[1];
        return new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
    }

    [Fact]
    public void NumericSlug_LeadingZeroPreserved()
    {
        // §2.6:PyYAML 與 js-yaml 對前導 0 判斷不一致 → 純數字型字串一律強制引號
        var block = Serialize(("slug", "01182024"));

        Assert.Contains("'01182024'", block);
        Assert.Equal("01182024", Roundtrip(block)["slug"]);
    }

    [Theory]
    [InlineData("20260712")]
    [InlineData("-42")]
    [InlineData("3.14")]
    [InlineData("1_000")]
    public void NumericLikeStrings_StayStrings(string value) =>
        Assert.Equal(value, Roundtrip(Serialize(("v", value)))["v"]);

    [Theory]
    [InlineData("no")]
    [InlineData("Off")]
    [InlineData("null")]
    public void BoolNullLikeStrings_StayStrings(string value) =>
        Assert.Equal(value, Roundtrip(Serialize(("v", value)))["v"]);

    [Fact]
    public void TitleWithColon_SurvivesRoundtrip()
    {
        // §2.6:標題含冒號 → 手工拼 YAML 會壞;一律走 YAML lib
        const string title = "2025/05/04 新聞:浴佛法會 — 圓滿";
        Assert.Equal(title, Roundtrip(Serialize(("title", title)))["title"]);
    }

    [Fact]
    public void FieldOrder_IsInsertionOrder()
    {
        var block = Serialize(("source_url", "https://x"), ("lang", "zh-Hant"), ("title", "t"));

        var lines = block.Split('\n');
        Assert.Equal("---", lines[0]);
        Assert.StartsWith("source_url:", lines[1]);
        Assert.StartsWith("lang:", lines[2]);
        Assert.StartsWith("title:", lines[3]);
    }

    [Fact]
    public void NestedImageList_Roundtrips()
    {
        var images = new List<Dictionary<string, string>>
        {
            new() { ["local"] = "/media/ch/news/images/a%20b.jpg", ["alt"] = "法會" },
        };
        var block = Serialize(("images", images));

        var parsed = Roundtrip(block);
        var list = Assert.IsAssignableFrom<IList<object>>(parsed["images"]);
        Assert.Single(list);
    }
}
