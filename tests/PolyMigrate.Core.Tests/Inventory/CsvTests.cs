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
    public void GuardDisabled_RoundTripsExactly()
    {
        // 內部快取用 guardFormulas:false,必須逐位元組還原
        var path = Path.Combine(_dir, "cache.csv");
        Csv.Write(path, [new[] { "-weird-name.jpg", "123", "abc" }], guardFormulas: false);

        var row = Csv.ReadRows(path).Single();
        Assert.Equal(["-weird-name.jpg", "123", "abc"], row);
    }
}
