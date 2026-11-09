using PolyMigrate.Core.Inventory;

namespace PolyMigrate.Core.Redirects;

/// <summary>轉址表的一條規則。<see cref="Line"/> 是檔案裡的行號(從 1 起,含表頭),報告點名用。</summary>
internal sealed record RedirectRule(int Line, string Source, string Target);

/// <summary>讀回的轉址表:能檢查的規則、刻意略過的條數(萬用字元、非 3xx)、讀不懂的行號。</summary>
internal sealed record RedirectTable(string Name, List<RedirectRule> Rules, int Skipped, List<int> Unreadable);

/// <summary>
/// 讀轉址表給 <c>verify --redirects</c> 離線檢查。兩種格式,依副檔名分:
/// <list type="bullet">
/// <item><c>.csv</c>:<c>redirect_map.csv</c>(<c>old_url</c> → <c>new_path</c>),<c>extract</c> 產出的那份;</item>
/// <item>其他:<c>_redirects</c>(Netlify / Cloudflare Pages:<c>來源 目標 [狀態碼]</c>,<c>#</c> 開頭是註解)——
/// 部署上線的是這份,手改也多半改這份。</item>
/// </list>
/// nginx 那份是同一批資料的另一種寫法,不另外讀。
/// </summary>
internal static class RedirectRules
{
    public static RedirectTable Load(string path)
    {
        var name = Path.GetFileName(path);
        return path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? LoadCsv(path, name) : LoadText(path, name);
    }

    private static RedirectTable LoadCsv(string path, string name)
    {
        var rows = Csv.ReadRows(path).ToList();
        var header = rows.Count > 0 ? rows[0].Select(h => h.Trim()).ToList() : [];
        var (from, to) = (header.IndexOf("old_url"), header.IndexOf("new_path"));
        if (from < 0 || to < 0)
        {
            throw new FormatException(
                $"{path}: a .csv redirect table needs the redirect_map.csv columns old_url and new_path.");
        }
        var rules = new List<RedirectRule>();
        var unreadable = new List<int>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(f => f.Trim().Length == 0))
            {
                continue;
            }
            var source = row.ElementAtOrDefault(from)?.Trim() ?? "";
            var target = row.ElementAtOrDefault(to)?.Trim() ?? "";
            if (source.Length == 0 || target.Length == 0)
            {
                unreadable.Add(i + 1);
                continue;
            }
            // old_url 是舊站的完整網址;新站上被比對的是路徑(含 query:?id=3 這類舊網址很常見)
            rules.Add(new RedirectRule(i + 1, Uri.TryCreate(source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                ? u.PathAndQuery
                : source, target));
        }
        return new RedirectTable(name, rules, 0, unreadable);
    }

    private static RedirectTable LoadText(string path, string name)
    {
        var rules = new List<RedirectRule>();
        var unreadable = new List<int>();
        var skipped = 0;
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2)
            {
                unreadable.Add(i + 1);
                continue;
            }
            // 狀態碼可帶 ! 強制(Netlify);200 是改寫、404 是自訂錯誤頁,都不是轉址
            if (f.Length >= 3 && int.TryParse(f[2].TrimEnd('!'), out var status) && status is < 300 or > 399)
            {
                skipped++;
                continue;
            }
            // 萬用字元(/news/*)與具名參數(/news/:id)一條規則對應很多網址,離線無從展開
            if (f[0].Contains('*') || f[0].Split('/').Any(s => s.StartsWith(':')) || f[1].Contains(":splat"))
            {
                skipped++;
                continue;
            }
            rules.Add(new RedirectRule(i + 1, f[0], f[1]));
        }
        return new RedirectTable(name, rules, skipped, unreadable);
    }
}
