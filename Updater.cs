using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeekPeakStatus;

public sealed record RefreshResult(bool Ok, List<string> Problems);

/// <summary>从 DeepSeek 官方文档页与公开节假日数据源拉取时段、价格和法定节假日。</summary>
public static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    private static readonly Regex TableRe = new(@"<table\b[\s\S]*?</table>", RegexOptions.IgnoreCase);
    private static readonly Regex RowRe = new(@"<tr\b[^>]*>([\s\S]*?)</tr>", RegexOptions.IgnoreCase);
    private static readonly Regex CellRe = new(@"<t([dh])\b[^>]*>([\s\S]*?)</t\1>", RegexOptions.IgnoreCase);
    private static readonly Regex TagRe = new(@"<[^>]*>");
    private static readonly Regex SpaceRe = new(@"\s+");
    private static readonly Regex ModelRe = new(@"^deepseek-", RegexOptions.IgnoreCase);
    private static readonly Regex ValueRe = new(@"^[\d.,]+\s*元$");
    private static readonly Regex WindowRe = new(@"(\d{1,2}):(\d{2})\s*[-–—~至]\s*(\d{1,2}):(\d{2})");
    private static readonly Regex FootNoteRe = new(@"<p>\s*\(2\)([\s\S]*?)</p>", RegexOptions.IgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeepSeekPeakStatus/1.0 (Windows)");
        return client;
    }

    private static async Task<string> FetchTextAsync(string url, int timeoutMs, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(Math.Max(1000, timeoutMs));
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotPublishedException("HTTP 404 未发布");
        }
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
        }
        return await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    private static string Decode(string text) => WebUtility.HtmlDecode(text);

    private static string StripTags(string html) => SpaceRe.Replace(Decode(TagRe.Replace(html, " ")), " ").Trim();

    private static List<string> CellsOf(string rowHtml)
    {
        var cells = new List<string>();
        foreach (Match m in CellRe.Matches(rowHtml))
        {
            cells.Add(StripTags(m.Groups[2].Value));
        }
        return cells;
    }

    private static List<List<string>> RowsOf(string tableHtml)
    {
        var rows = new List<List<string>>();
        foreach (Match m in RowRe.Matches(tableHtml))
        {
            rows.Add(CellsOf(m.Groups[1].Value));
        }
        return rows;
    }

    /// <summary>只认"百万 tokens 输入/输出"这种完整标签，避免被同表的"输出长度"误判。</summary>
    private static string? MetricOf(string text)
    {
        if (Regex.IsMatch(text, @"百万\s*tokens\s*输入[\s\S]*缓存命中"))
        {
            return "cacheHit";
        }
        if (Regex.IsMatch(text, @"百万\s*tokens\s*输入[\s\S]*缓存未命中"))
        {
            return "cacheMiss";
        }
        return Regex.IsMatch(text, @"百万\s*tokens\s*输出") ? "output" : null;
    }

    public static PricingCache ParsePricing(string html, string url)
    {
        var tableMatch = TableRe.Match(html);
        if (!tableMatch.Success)
        {
            throw new InvalidOperationException("页面结构没有找到价格表格");
        }
        var rows = RowsOf(tableMatch.Value);

        // 表头行里以 deepseek- 开头的单元格就是模型名
        var header = rows.FirstOrDefault(r => r.Any(c => ModelRe.IsMatch(c)));
        var models = header == null
            ? new List<string>()
            : header.Where(c => ModelRe.IsMatch(c))
                    .Select(c => Regex.Replace(c, @"\(\d+\)", "").Trim())
                    .ToList();
        if (models.Count == 0)
        {
            throw new InvalidOperationException("页面结构没有找到模型列表");
        }

        var prices = new PriceSet
        {
            Models = models.ToDictionary(m => m, m => new ModelPrice { Label = m })
        };

        // 表格用 rowspan 合并同一指标的峰/空闲两行：指标名只出现在第一行，按行序继承；
        // 解析从"价格"那一行开始，到"并发限制"结束
        bool inPriceSection = false;
        string? metric = null;
        int filled = 0;

        foreach (var row in rows)
        {
            if (row.Count == 0)
            {
                continue;
            }
            var joined = string.Join(" ", row);
            if (!inPriceSection)
            {
                if (!row[0].Contains("价格"))
                {
                    continue;
                }
                inPriceSection = true;
            }
            if (joined.Contains("并发限制"))
            {
                break;
            }
            metric = MetricOf(joined) ?? metric;
            if (metric == null)
            {
                continue;
            }
            bool? peak = joined.Contains("空闲时段") ? false : joined.Contains("高峰时段") ? true : null;
            if (peak == null)
            {
                continue;
            }
            var values = row.Where(c => ValueRe.IsMatch(c))
                            .Select(c => Regex.Replace(c, @"\s*元$", "").Trim())
                            .ToList();
            if (values.Count < models.Count)
            {
                continue;
            }
            for (int i = 0; i < models.Count; i++)
            {
                var tier = peak.Value ? prices.Models[models[i]].Peak : prices.Models[models[i]].OffPeak;
                switch (metric)
                {
                    case "cacheHit": tier.CacheHit = values[i]; break;
                    case "cacheMiss": tier.CacheMiss = values[i]; break;
                    case "output": tier.Output = values[i]; break;
                }
            }
        }

        foreach (var id in models)
        {
            var m = prices.Models[id];
            if (!m.Peak.IsEmpty && !m.OffPeak.IsEmpty)
            {
                filled++;
            }
        }
        if (filled == 0)
        {
            throw new InvalidOperationException("页面结构解析出的价格为空");
        }

        // 脚注(2)："北京时间周一至周五（不含中国法定节假日）9:00 - 12:00、14:00 - 18:00 为高峰时段"
        var note = FootNoteRe.Match(html);
        var source = note.Success ? StripTags(note.Groups[1].Value) : StripTags(html);
        var windows = new List<string>();
        foreach (Match m in WindowRe.Matches(source))
        {
            var value = $"{int.Parse(m.Groups[1].Value):D2}:{m.Groups[2].Value}-{int.Parse(m.Groups[3].Value):D2}:{m.Groups[4].Value}";
            if (!windows.Contains(value))
            {
                windows.Add(value);
            }
        }
        if (windows.Count == 0)
        {
            throw new InvalidOperationException("页面结构没有找到峰时区间");
        }

        return new PricingCache
        {
            Prices = prices,
            Windows = windows,
            WeekendOffPeak = Regex.IsMatch(source, @"周末[\s\S]*空闲"),
            ScheduleText = source,
            FetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Url = url
        };
    }

    public static HolidayCache ParseHolidays(string json, int year, string url)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("days", out var days) || days.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{year} 年节假日数据格式不正确");
        }
        var dates = new List<string>();
        foreach (var day in days.EnumerateArray())
        {
            if (day.TryGetProperty("isOffDay", out var off) && off.ValueKind == JsonValueKind.True &&
                day.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
            {
                dates.Add(d.GetString()!);
            }
        }
        if (dates.Count == 0)
        {
            // 源里会提前放一个空占位文件（HTTP 200），等同于"尚未公布"
            throw new NotPublishedException($"{year} 年节假日尚未公布");
        }
        string source = "";
        if (doc.RootElement.TryGetProperty("papers", out var papers) && papers.ValueKind == JsonValueKind.Array && papers.GetArrayLength() > 0)
        {
            source = papers[0].GetString() ?? "";
        }
        return new HolidayCache
        {
            Dates = dates,
            Years = new List<int> { year },
            Source = source,
            FetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Url = url
        };
    }

    /// <summary>依次尝试主源与镜像；404 说明该年份还没发布，换镜像也没用。</summary>
    public static async Task<HolidayCache> FetchHolidayYearAsync(int year, string template, int timeoutMs, CancellationToken token = default)
    {
        var templates = new List<string>();
        if (!string.IsNullOrWhiteSpace(template))
        {
            templates.Add(template);
        }
        else
        {
            templates.Add("https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json");
        }
        foreach (var extra in new[]
        {
            "https://fastly.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json",
            "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json"
        })
        {
            if (!templates.Contains(extra))
            {
                templates.Add(extra);
            }
        }

        var errors = new List<string>();
        foreach (var t in templates)
        {
            var url = t.Replace("{year}", year.ToString());
            try
            {
                var body = await FetchTextAsync(url, timeoutMs, token).ConfigureAwait(false);
                return ParseHolidays(body, year, url);
            }
            catch (NotPublishedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{HostOf(url)}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(string.Join(" | ", errors));
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private static bool IsStale(long fetchedAt, int intervalHours) =>
        fetchedAt <= 0 || intervalHours <= 0 || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - fetchedAt >= intervalHours * 3600_000L;

    /// <summary>按需刷新价格/时段与节假日；全部失败时保留上次可用数据。</summary>
    public static async Task<RefreshResult> RefreshAsync(Store store, bool force, CancellationToken token = default)
    {
        var cfg = store.Config;
        var cache = store.Cache;
        var problems = new List<string>();
        bool changed = false;

        var effective = Peak.Resolve(cfg, cache);
        int currentYear = int.Parse(Peak.Build(effective).DateKey[..4]);

        if (force || IsStale(cache.Pricing?.FetchedAt ?? 0, cfg.PricingIntervalHours))
        {
            try
            {
                var html = await FetchTextAsync(cfg.PricingUrl, cfg.TimeoutMs, token).ConfigureAwait(false);
                cache.Pricing = ParsePricing(html, cfg.PricingUrl);
                cache.Pricing.Prices.Currency = "CNY";
                cache.Pricing.Prices.Unit = "元 / 百万 tokens";
                changed = true;
            }
            catch (Exception ex)
            {
                problems.Add($"价格/时段：{ex.Message}");
            }
        }

        // 只取当前年份：下一年的安排要到当年年底才公布
        bool coversYear = cache.Holidays != null && cache.Holidays.Years.Contains(currentYear);
        if (force || !coversYear || IsStale(cache.Holidays?.FetchedAt ?? 0, cfg.HolidayIntervalHours))
        {
            try
            {
                var holiday = await FetchHolidayYearAsync(currentYear, cfg.HolidayUrlTemplate, cfg.TimeoutMs, token).ConfigureAwait(false);
                cache.Holidays = holiday;
                changed = true;
            }
            catch (NotPublishedException ex)
            {
                problems.Add($"{currentYear} 年节假日：{ex.Message}");
            }
            catch (Exception ex)
            {
                problems.Add($"{currentYear} 年节假日：{ex.Message}");
            }
        }

        if (changed)
        {
            store.SaveCache();
        }
        return new RefreshResult(problems.Count == 0, problems);
    }
}

public sealed class NotPublishedException : Exception
{
    public NotPublishedException(string message) : base(message) { }
}
