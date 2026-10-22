using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Tests.Inventory;

public class CsvTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("polymigrate-csv").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IReadOnlyList<string> WriteAndReadBack(bool guardFormulas, params string[] fields)
    {
        var path = Path.Combine(_dir, "t.csv");
        Csv.Write(path, [fields], guardFormulas);
        return Csv.ReadRows(path).Single();
    }

    [Theory]
    // 試算表公式注入:= + - @ 開頭的欄位在 Excel/Sheets 會被當公式執行 → 前置單引號中和
    // (讀回的欄位值本身帶 '——RFC-4180 引號在外層另計,故對還原值判斷才穩)
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+1+2")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void FormulaLeadingFields_Guarded(string dangerous)
    {
        var row = WriteAndReadBack(guardFormulas: true, dangerous, "safe");
        Assert.Equal("'" + dangerous, row[0]);
        Assert.Equal("safe", row[1]);
    }

    [Fact]
    public void OrdinaryFields_NotAltered()
    {
        var row = WriteAndReadBack(guardFormulas: true, "/media/x.jpg", "https://h/a", "標題");
        Assert.Equal(["/media/x.jpg", "https://h/a", "標題"], row);
    }

    [Fact]
    public void SpecialCharacters_QuotedPerRfc4180()
    {
        // 逐位元組驗跳脫:含 , " \n \r 的欄位整欄加雙引號、內部 " 變 "";其餘原樣
        var path = Path.Combine(_dir, "q.csv");
        Csv.Write(path, [new[] { "a,b", "say \"hi\"", "line1\nline2", "cr\rhere", "plain", "" }]);

        var text = File.ReadAllText(path);
        Assert.Equal("\"a,b\",\"say \"\"hi\"\"\",\"line1\nline2\",\"cr\rhere\",plain,\r\n", text);
    }

    [Fact]
    public void SpecialCharacters_RoundTrip()
    {
        // a"b:單個引號夾在中間。成對的 say "hi" 在「"" 之後漏跳過第二個引號」的錯誤解析下也能剛好還原,
        // 單個的不行——漏跳過會讓解析器在 b 之後重新進入引號狀態,吃掉後面的逗號與換行
        string[] fields = ["a,b", "say \"hi\"", "a\"b", "line1\nline2", "crlf\r\ninside", "標題,含逗號", ""];

        var row = WriteAndReadBack(guardFormulas: false, fields);

        Assert.Equal(fields, row);
    }

    [Theory]
    [InlineData("\tindented")]   // 前導 tab / CR 在 Excel 一樣會被當成公式起頭
    [InlineData("\rcarriage")]
    public void TabOrCrLeadingFields_AlsoGuarded(string dangerous)
    {
        var row = WriteAndReadBack(guardFormulas: true, dangerous);

        Assert.Equal("'" + dangerous, row[0]);
    }

    [Fact]
    public void ReadRows_Utf8Bom_IsNotPartOfTheFirstField()
    {
        // 本工具寫的 CSV 都含 BOM;讀回時第一欄不可以帶著它(verify 讀 missing_images.csv、快取讀自己)
        var path = Path.Combine(_dir, "bom.csv");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "h1,h2\r\na,b\r\n"u8.ToArray()]);

        var rows = Csv.ReadRows(path).ToList();

        Assert.Equal(["h1", "h2"], rows[0]);
    }

    [Fact]
    public void ReadRows_LastRowWithoutTrailingNewline_IsStillRead()
    {
        // Excel 存回的檔常常沒有結尾換行;最後一列不能因此被吃掉
        var path = Path.Combine(_dir, "edited.csv");
        File.WriteAllText(path, "\uFEFFh1,h2\r\na,b\r\nc,d");

        var rows = Csv.ReadRows(path).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(["c", "d"], rows[2]);
    }

    [Fact]
    public void ReadRows_AcceptsBareLfLineEndings()
    {
        // 人工用 LF 編輯器改過的檔(或 CRLF checkout 被轉過)也要讀得回來
        var path = Path.Combine(_dir, "lf.csv");
        File.WriteAllText(path, "h1,h2\na,b\n");

        Assert.Equal([["h1", "h2"], ["a", "b"]], Csv.ReadRows(path).Select(r => r.ToArray()));
    }

    [Fact]
    public void GuardDisabled_RoundTripsExactly()
    {
        // 內部快取用 guardFormulas:false,必須逐位元組還原
        var path = Path.Combine(_dir, "cache.csv");
        Csv.Write(path, [new[] { "-weird-name.jpg", "123", "abc" }], guardFormulas: false);

        var row = Csv.ReadRows(path).Single();
        Assert.Equal(["-weird-name.jpg", "123", "abc"], row);
    }
}
