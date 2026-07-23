using System.Text;

namespace PolyMigrate.Core.Inventory;

/// <summary>
/// 極簡 CSV 輸出:RFC 4180 跳脫、\r\n 行尾、UTF-8 含 BOM(人工覆核走 Excel,無 BOM 中文會亂碼)。
/// </summary>
internal static class Csv
{
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <param name="guardFormulas">
    /// true(預設,人工在 Excel 覆核的清單):對 = + - @ 開頭的欄位前置單引號,擋掉試算表公式注入
    /// (爬來的 alt/URL 可能是 =HYPERLINK(...) 之類)。
    /// false:本工具自己讀回的內部檔(如媒體雜湊快取),需逐位元組還原、不可加料。
    /// </param>
    public static void Write(string path, IEnumerable<IReadOnlyList<string>> rows, bool guardFormulas = true)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(f => Escape(f, guardFormulas)))).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), Utf8Bom);
    }

    private static string Escape(string field, bool guardFormulas)
    {
        // 試算表公式注入防護:欄位若以 = + - @(或前導 tab/CR)起頭,Excel/Sheets 會當公式執行
        if (guardFormulas && field.Length > 0 && "=+-@\t\r".IndexOf(field[0]) >= 0)
        {
            field = "'" + field;
        }
        return field.AsSpan().IndexOfAny(',', '"', '\n') >= 0 || field.Contains('\r')
            ? "\"" + field.Replace("\"", "\"\"") + "\""
            : field;
    }

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
