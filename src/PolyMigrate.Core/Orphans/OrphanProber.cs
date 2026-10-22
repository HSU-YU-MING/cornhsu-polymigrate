using System.Globalization;
using PolyMigrate.Core.Configuration;

namespace PolyMigrate.Core.Orphans;

/// <summary>
/// 探測孤兒頁的結果(<see cref="OrphanProber.ProbeDetailedAsync"/>、<see cref="PolyMigrator.ProbeOrphansAsync(string, string, int, int, string?, Action{string}?, CancellationToken)"/>)。
/// 分開統計「確定沒有」與「沒拿到回答」:伺服器掛了、被 WAF 擋、<c>base_url</c> 打錯時,<see cref="Found"/> 一樣是空的,
/// 但那不代表沒有孤兒頁——看 <see cref="NoUsableAnswers"/>。3.0 起 public(2.x 是 internal 的 ProbeResult)。
/// </summary>
public sealed class ProbeReport
{
    /// <summary>只由探測產生;呼叫端拿到的都是填好的結果。</summary>
    internal ProbeReport()
    {
    }

    internal List<string> FoundList { get; } = [];

    internal SortedDictionary<string, int> UnansweredByReason { get; } = new(StringComparer.Ordinal);

    /// <summary>命中的 slug,依探測順序(= <see cref="OrphanProber.ProbeAsync"/> 的回傳值)。</summary>
    public IReadOnlyList<string> Found => FoundList;

    /// <summary>探過的候選數(含後綴變體;每個候選最多兩次 HTTP 請求,這裡只算一次)。</summary>
    public int Checks { get; internal set; }

    /// <summary>
    /// 沒拿到「有/沒有」回答的候選,依原因計數(<c>HTTP 503</c>、<c>connection error</c>、<c>timeout</c>…),鍵依 ordinal 排序。
    /// 「確定沒有」的回答是 404、410、其他 2xx 與 3xx(不跟轉址);401/403/429/5xx、重試後仍 409、連線錯誤與逾時算沒拿到回答。
    /// </summary>
    public IReadOnlyDictionary<string, int> Unanswered => UnansweredByReason;

    /// <summary><see cref="Unanswered"/> 的總數。</summary>
    public int UnansweredCount => UnansweredByReason.Values.Sum();

    /// <summary>
    /// 一個回答都沒拿到(探了至少一個候選,而且全部沒拿到回答)。這時 <see cref="Found"/> 是空的,但不可讀成
    /// 「沒有孤兒頁」:CLI 的 <c>probe-orphans</c> 在這個情況 exit 2、不覆寫既有清單。
    /// </summary>
    public bool NoUsableAnswers => Checks > 0 && UnansweredCount == Checks;
}

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

    /// <summary>
    /// 已抓到的 slug(<c>raw/{lang}/{section}/</c> 的檔名),探測時跳過。
    /// <para>這個多載不帶 config,所以檔名 → slug 用的是啟發式:去掉結尾的 <c>.html</c>,再去掉一段 1～8 個 ASCII 英數字、
    /// 至少一個字母的副檔名(<c>.php</c>、<c>.php5</c>、<c>.asp</c>…;2.3.1 以前是取第一個點之前)。它與 <c>extract</c>
    /// 的 slug 在這些情況不一致:乾淨網址站的 <c>jane.doe.html</c>(這裡得到 <c>jane</c>);<c>strip_extensions: []</c> 的
    /// <c>foo.php.html</c>(這裡得到 <c>foo</c>);副檔名不在 <c>strip_extensions</c> 裡的(只列 <c>.php</c> 時的
    /// <c>a.php5.html</c>、<c>a.asp.html</c>,這裡得到 <c>a</c>);大小寫不符的(<c>A.PHP.html</c>,這裡得到 <c>A</c>)。
    /// 手上有 config 時用帶 <c>stripExtensions</c> 的多載,結果與 <c>extract</c> 及 CLI 的 <c>probe-orphans</c> 一致。</para>
    /// </summary>
    public static HashSet<string> KnownSlugs(string rawDir, string langPrefix, string section) =>
        Inventory.MirrorSlugs.ForLanguage(rawDir, langPrefix, section);

    /// <summary>
    /// 同上,但依 config 的 <c>url_pattern.strip_extensions</c> 剝檔名尾綴(傳 <c>config.UrlPattern.StripExtensions</c>):
    /// 與 <c>extract</c> 寫出的 slug 完全相同的規則,CLI 的 <c>probe-orphans</c> 用的就是這個(3.0 起 public)。
    /// </summary>
    public static HashSet<string> KnownSlugs(string rawDir, string langPrefix, string section, IReadOnlyList<string> stripExtensions) =>
        Inventory.MirrorSlugs.ForLanguage(rawDir, langPrefix, section, stripExtensions);

    public async Task<List<string>> ProbeAsync(
        string langPrefix, string section, int yearFrom, int yearTo,
        IReadOnlySet<string> known, Action<string>? progress = null, CancellationToken ct = default) =>
        (await ProbeDetailedAsync(langPrefix, section, yearFrom, yearTo, known, progress, ct)).FoundList;

    /// <summary>
    /// 同 <see cref="ProbeAsync"/>,另外分開統計「確定沒有」與「沒拿到回答」。
    /// 兩者原本一律當成「不存在」:伺服器掛了、被 WAF 擋、base_url 打錯時,探測照樣「跑完、0 筆」,
    /// 而空清單會被讀成「沒有孤兒頁」。CLI 靠這份統計決定清單能不能信、要不要寫(3.0 起 public)。
    /// </summary>
    public async Task<ProbeReport> ProbeDetailedAsync(
        string langPrefix, string section, int yearFrom, int yearTo,
        IReadOnlySet<string> known, Action<string>? progress = null, CancellationToken ct = default)
    {
        var result = new ProbeReport();
        var found = result.FoundList;
        var probed = 0;

        async Task<bool> Exists(string slug)
        {
            var (answer, reason) = await ProbeOneAsync(langPrefix, section, slug, ct);
            result.Checks++;
            if (answer == Answer.None)
            {
                result.UnansweredByReason[reason!] = result.UnansweredByReason.GetValueOrDefault(reason!) + 1;
            }
            return answer == Answer.Exists;
        }

        foreach (var candidate in DateCandidates(yearFrom, yearTo))
        {
            // 已鏡像 = 確定存在:本身不探,但後綴照探——後綴版是不是孤兒,與原頁有沒有鏡像無關
            // (原本在這裡直接 continue,「原頁已鏡像、A 版是孤兒」永遠找不到,而那正是 probe 要找的)
            var isKnown = known.Contains(candidate);
            var exists = isKnown;
            if (!isKnown)
            {
                probed++;
                exists = await Exists(candidate);
                if (exists)
                {
                    found.Add(candidate);
                    progress?.Invoke($"hit {candidate}");
                }
            }
            if (exists)
            {
                foreach (var suffix in Suffixes)
                {
                    var variant = candidate + suffix;
                    if (known.Contains(variant))
                    {
                        continue;   // 已鏡像:存在,不探、不列,「A 有才會有 B」的鏈繼續往下
                    }
                    if (await Exists(variant))
                    {
                        found.Add(variant);
                        progress?.Invoke($"hit {variant}");
                    }
                    else
                    {
                        break;
                    }
                }
            }
            if (!isKnown && probed % 100 == 0)
            {
                progress?.Invoke($"probed {probed}, hits {found.Count}");
            }
        }
        return result;
    }

    private enum Answer
    {
        /// <summary>200。</summary>
        Exists,

        /// <summary>確定沒有:404、410、其他 2xx,以及 3xx(不跟轉址,轉址頁不算命中,見 <see cref="CreateHttpClient"/>)。</summary>
        Absent,

        /// <summary>沒拿到「有沒有」的回答:連線錯誤、逾時、重試後仍 409,以及 401/403/429/5xx 等其餘狀態。</summary>
        None,
    }

    /// <returns>回答;<see cref="Answer.None"/> 時附上原因(<c>HTTP 503</c>、<c>connection error</c>、<c>timeout</c>)。</returns>
    private async Task<(Answer Answer, string? Reason)> ProbeOneAsync(
        string langPrefix, string section, string slug, CancellationToken ct)
    {
        var url = $"{_baseUrl}/{SitePaths.Join(langPrefix, section, slug)}{_extension}";   // 無語言前綴的站不可組出 //news/…
        var reason = "";   // 走到迴圈外 = 最後一次是例外,原因一定已經填上
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Task.Delay(config.Site.Polite.DelayMs * (attempt + 1), ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await http.SendAsync(request, ct);
                var status = (int)response.StatusCode;
                if (status == 409 && attempt == 0)
                {
                    continue;   // 409 = bot 防護,退避加倍再試一次
                }
                return status switch
                {
                    200 => (Answer.Exists, null),
                    404 or 410 or (>= 200 and < 400) => (Answer.Absent, null),
                    _ => (Answer.None, $"HTTP {status}"),
                };
            }
            catch (Exception ex) when (ex is HttpRequestException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // 連線層錯誤或請求逾時(非使用者取消):重試一次後放棄該候選,不掀掉整批探測
                reason = ex is HttpRequestException ? "connection error" : "timeout";
            }
        }
        return (Answer.None, reason);
    }

    /// <summary>
    /// 建構帶 UA 與 auth_workaround cookie 的 HttpClient。
    /// probe 不跟轉址(轉址頁不算命中);fetch 要跟(頁面可能在 301 之後)。
    /// </summary>
    public static HttpClient CreateHttpClient(SiteConfig config, bool allowRedirects = false)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = allowRedirects })
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
