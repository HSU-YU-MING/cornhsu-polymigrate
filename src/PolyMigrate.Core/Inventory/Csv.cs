using System.Text;

namespace PolyMigrate.Core.Inventory;

/// <summary>
/// 極簡 CSV 輸出:RFC 4180 跳脫、\r\n 行尾、UTF-8 含 BOM(人工覆核走 Excel,無 BOM 中文會亂碼)。
/// </summary>
internal static class Csv
{
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static void Write(string path, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    private static string Escape(string field) =>
        field.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || field.Contains('\r')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
}
