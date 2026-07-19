using System.Globalization;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Orphans;

/// <summary>
/// 孤兒頁探測(規格 probe_orphans.py;§2.6:索引移除但頁面還在)。
/// 逐日期產生候選 slug(YYYYMMDD 與 MMDDYYYY 兩種格式都探,§2.6 日期混用坑),
/// HEAD 探測原站;命中再探 A-D 後綴變體(A 沒有就不會有 B)。
/// 禮貌:每請求間隔 site.polite.delay_ms;409(bot 防護)退避加倍重試一次。
/// </summary>
public sealed class OrphanProber(SiteConfig config, HttpClient http)
{
    private readonly string _baseUrl = config.Site.BaseUrl.TrimEnd('/');
    private readonly string _extension = config.UrlPattern.StripExtensions.FirstOrDefault() ?? "";

    private static readonly string[] Suffixes = ["A", "B", "C", "D"];

    /// <summary>逐日期候選 slug:每一天先 MMDDYYYY 再 YYYYMMDD(與規格同序)。</summary>
    public static IEnumerable<string> DateCandidates(int yearFrom, int yearTo)
    {
        for (var year = yearFrom; year <= yearTo; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                for (var day = 1; day <= CultureInfo.InvariantCulture.Calendar.GetDaysInMonth(year, month); day++)
                {
                    yield return $"{month:00}{day:00}{year}";
                    yield return $"{year}{month:00}{day:00}";
                }
            }
        }
    }

    /// <summary>已抓到的 slug(raw/{lang}/{section}/ 檔名),探測時跳過。</summary>
    public static HashSet<string> KnownSlugs(string rawDir, string langPrefix, string section)
    {
        var dir = Path.Combine(rawDir, langPrefix, section);
        if (!Directory.Exists(dir))
        {
            return [];
        }
        return [.. Directory.EnumerateFiles(dir)
            .Select(f => Path.GetFileName(f))
            .Select(f => f.Split('.')[0])];
    }

    public async Task<List<string>> ProbeAsync(
        string langPrefix, string section, int yearFrom, int yearTo,
        IReadOnlySet<string> known, Action<string>? progress = null, CancellationToken ct = default)
    {
        var found = new List<string>();
        var probed = 0;
        foreach (var candidate in DateCandidates(yearFrom, yearTo))
        {
            if (known.Contains(candidate))
            {
                continue;
            }
            probed++;
            if (await ExistsAsync(langPrefix, section, candidate, ct))
            {
                found.Add(candidate);
                progress?.Invoke($"hit {candidate}");
                foreach (var suffix in Suffixes)
                {
                    if (await ExistsAsync(langPrefix, section, candidate + suffix, ct))
                    {
                        found.Add(candidate + suffix);
                        progress?.Invoke($"hit {candidate}{suffix}");
                    }
                    else
                    {
                        break;
                    }
                }
            }
            if (probed % 100 == 0)
            {
                progress?.Invoke($"probed {probed}, hits {found.Count}");
            }
        }
        return found;
    }

    private async Task<bool> ExistsAsync(string langPrefix, string section, string slug, CancellationToken ct)
    {
        var url = $"{_baseUrl}/{langPrefix}/{section}/{slug}{_extension}";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(config.Site.Polite.DelayMs * (attempt + 1), ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await http.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict && attempt == 0)
                {
                    continue;   // 409 = bot 防護,退避加倍再試一次
                }
                return response.StatusCode == System.Net.HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                // 連線層錯誤:重試一次後放棄該候選
            }
        }
        return false;
    }

    /// <summary>建構帶 UA 與 auth_workaround cookie 的 HttpClient(probe/fetch 共用)。</summary>
    public static HttpClient CreateHttpClient(SiteConfig config)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(40),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", config.Site.UserAgent);
        if (config.Site.AuthWorkaround is { Type: "cookie", Set.Count: > 0 } auth)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie",
                string.Join("; ", auth.Set.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        return client;
    }
}
