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

    /// <summary>讀回本工具寫出的 CSV(RFC 4180,含引號跳脫;自動吃掉 BOM)。</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadRows(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }
}
