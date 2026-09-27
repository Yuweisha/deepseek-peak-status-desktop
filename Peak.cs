namespace DeepSeekPeakStatus;

/// <summary>峰时区间，单位是"当天第几分钟"。</summary>
public readonly record struct Window(int Start, int End);

public readonly record struct Segment(int Start, int End, bool Peak);

public sealed class Change
{
    public bool Peak { get; init; }
    public int MinuteOfDay { get; init; }
    public int Weekday { get; init; }
    public int DayOffset { get; init; }
    public string DateKey { get; init; } = "";
    public double Seconds { get; init; }
}

/// <summary>配置 + 缓存合并后的、真正用于计算的设置。</summary>
public sealed class EffectiveConfig
{
    public List<Window> Windows { get; init; } = new();
    public List<string> RawWindows { get; init; } = new();
    public HashSet<string> Holidays { get; init; } = new();
    public bool WeekendOffPeak { get; init; } = true;
    public bool ScheduleFromWeb { get; init; }
    public bool HolidaysFromWeb { get; init; }
    public bool AutoUpdateEnabled { get; init; } = true;
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    public PriceSet Prices { get; init; } = new();
    public bool NotifyEnabled { get; init; } = true;
    public int NotifyLeadMinutes { get; init; } = 3;
    public string PricingUrl { get; init; } = "";
    public PricingCache? PricingCache { get; init; }
    public HolidayCache? HolidayCache { get; init; }
    public AppConfig Raw { get; init; } = new();
}

public sealed class Snapshot
{
    public int Weekday { get; init; }
    public string DateKey { get; init; } = "";
    public int Minutes { get; init; }
    public int Second { get; init; }
    public string DayKind { get; init; } = "";
    public List<Segment> Segments { get; init; } = new();
    public Segment Current { get; init; }
    public bool AllDayOff { get; init; }
    public double? Remaining { get; init; }
    public bool? NextPeak { get; init; }
    public string NextLabel { get; init; } = "";
    public string? ChangeKey { get; init; }

    public bool IsPeak => Current.Peak;
    public string TierName => Current.Peak ? "峰时" : "空闲";
}

public static class Peak
{
    private const int LookaheadDays = 14;
    private static readonly string[] WeekdayCn = { "日", "一", "二", "三", "四", "五", "六" };

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
            catch { /* 非 Windows 或缺少时区库时退回本地时区 */ }
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Local; }
    }

    /// <summary>"HH:mm-HH:mm" → [start, end)，跨零点拆成两段。</summary>
    public static List<Window> ParseWindows(IEnumerable<string>? list)
    {
        var result = new List<Window>();
        if (list == null)
        {
            return result;
        }
        foreach (var item in list)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                (item ?? "").Trim(), @"^(\d{1,2}):(\d{2})\s*[-~－—]\s*(\d{1,2}):(\d{2})$");
            if (!m.Success)
            {
                continue;
            }
            int start = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
            int end = int.Parse(m.Groups[3].Value) * 60 + int.Parse(m.Groups[4].Value);
            if (start >= 1440 || end > 1440 || start == end)
            {
                continue;
            }
            if (end < start)
            {
                result.Add(new Window(start, 1440));
                result.Add(new Window(0, end));
            }
            else
            {
                result.Add(new Window(start, end));
            }
        }
        return result;
    }

    /// <summary>支持 "YYYY-MM-DD" 与每年重复的 "MM-DD"。</summary>
    public static HashSet<string> ParseHolidays(IEnumerable<string>? list)
    {
        var set = new HashSet<string>();
        if (list == null)
        {
            return set;
        }
        foreach (var item in list)
        {
            var s = (item ?? "").Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$") ||
                System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{2}-\d{2}$"))
            {
                set.Add(s);
            }
        }
        return set;
    }

    private static (int Weekday, string DateKey, int Minutes, int Second) ZonedNow(TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        return ((int)local.DayOfWeek, local.ToString("yyyy-MM-dd"), local.Hour * 60 + local.Minute, local.Second);
    }

    private static bool IsHoliday(string dateKey, EffectiveConfig cfg) =>
        cfg.Holidays.Contains(dateKey) || cfg.Holidays.Contains(dateKey.Substring(5));

    public static string DayKind(int weekday, string dateKey, EffectiveConfig cfg)
    {
        if (IsHoliday(dateKey, cfg))
        {
            return "法定节假日";
        }
        return weekday is 0 or 6 ? "周末" : "工作日";
    }

    /// <summary>周末或法定节假日全天按空闲时段计。</summary>
    private static bool IsOffPeakDay(int weekday, string dateKey, EffectiveConfig cfg) =>
        (cfg.WeekendOffPeak && weekday is 0 or 6) || IsHoliday(dateKey, cfg);

    private static bool IsPeak(int minute, int weekday, string dateKey, EffectiveConfig cfg)
    {
        if (IsOffPeakDay(weekday, dateKey, cfg))
        {
            return false;
        }
        foreach (var w in cfg.Windows)
        {
            if (minute >= w.Start && minute < w.End)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>当天按峰/空闲切分的连续区间。</summary>
    public static List<Segment> DaySegments(EffectiveConfig cfg, int weekday, string dateKey)
    {
        var segs = new List<Segment>();
        bool peak = IsPeak(0, weekday, dateKey, cfg);
        int start = 0;
        for (int m = 1; m <= 1440; m++)
        {
            bool? next = m < 1440 ? IsPeak(m, weekday, dateKey, cfg) : null;
            if (next != peak)
            {
                segs.Add(new Segment(start, m, peak));
                start = m;
                peak = next ?? false;
            }
        }
        return segs;
    }

    /// <summary>
    /// 往前找真正发生切换的那一分钟：周末/节假日连着整天空闲时午夜并不切换，
    /// 不能拿"到 24:00 的剩余时间"当倒计时。
    /// </summary>
    private static Change? FindNextChange(EffectiveConfig cfg, int weekday, string dateKey, int minutes, int second)
    {
        bool currentPeak = IsPeak(minutes, weekday, dateKey, cfg);
        var today = DateOnly.ParseExact(dateKey, "yyyy-MM-dd");
        for (int offset = 1; offset <= LookaheadDays * 1440; offset++)
        {
            int total = minutes + offset;
            int dayOffset = total / 1440;
            int minuteOfDay = total % 1440;
            var date = today.AddDays(dayOffset);
            var key = date.ToString("yyyy-MM-dd");
            if (IsPeak(minuteOfDay, (int)date.DayOfWeek, key, cfg) != currentPeak)
            {
                return new Change
                {
                    Peak = !currentPeak,
                    MinuteOfDay = minuteOfDay,
                    Weekday = (int)date.DayOfWeek,
                    DayOffset = dayOffset,
                    DateKey = key,
                    Seconds = offset * 60.0 - second
                };
            }
        }
        return null;
    }

    public static string FormatMinute(int minute) => $"{minute / 60:D2}:{minute % 60:D2}";

    public static string FormatDuration(double total)
    {
        var s = (int)Math.Max(0, Math.Round(total));
        return $"{s / 3600:D2}:{s % 3600 / 60:D2}:{s % 60:D2}";
    }

    /// <summary>跨天时用 "N天 HH:MM"，否则 HH:MM:SS。</summary>
    public static string FormatRemaining(double total)
    {
        var s = (int)Math.Max(0, Math.Round(total));
        int days = s / 86400;
        if (days > 0)
        {
            int rest = s - days * 86400;
            return $"{days}天 {rest / 3600:D2}:{rest % 3600 / 60:D2}";
        }
        return FormatDuration(s);
    }

    public static string ChangeLabel(Change? change)
    {
        if (change == null)
        {
            return "无峰谷切换";
        }
        string prefix = change.DayOffset switch { 0 => "今天", 1 => "明天", _ => $"周{WeekdayCn[change.Weekday]}" };
        return $"{prefix} {FormatMinute(change.MinuteOfDay)}";
    }

    public static string WeekdayName(int weekday) => $"周{WeekdayCn[weekday]}";

    public static Snapshot Build(EffectiveConfig cfg)
    {
        var (weekday, dateKey, minutes, second) = ZonedNow(cfg.TimeZone);
        var segs = DaySegments(cfg, weekday, dateKey);
        var current = segs[^1];
        foreach (var seg in segs)
        {
            if (minutes >= seg.Start && minutes < seg.End)
            {
                current = seg;
                break;
            }
        }
        var change = FindNextChange(cfg, weekday, dateKey, minutes, second);
        return new Snapshot
        {
            Weekday = weekday,
            DateKey = dateKey,
            Minutes = minutes,
            Second = second,
            DayKind = DayKind(weekday, dateKey, cfg),
            Segments = segs,
            Current = current,
            AllDayOff = !current.Peak && segs.Count == 1,
            Remaining = change?.Seconds,
            NextPeak = change?.Peak,
            NextLabel = ChangeLabel(change),
            ChangeKey = change == null ? null : $"{change.DateKey}#{change.MinuteOfDay}#{(change.Peak ? "peak" : "off")}"
        };
    }

    /// <summary>把配置与缓存合并成实际生效的设置，和扩展的 getConfig() 一致。</summary>
    public static EffectiveConfig Resolve(AppConfig c, CacheFile cache)
    {
        var tz = ResolveTimeZone(c.TimeZone);
        bool scheduleFromWeb = c.AutoUpdateEnabled && cache.Pricing != null
            && tz.Id.Equals(ResolveTimeZone("Asia/Shanghai").Id, StringComparison.OrdinalIgnoreCase);
        var rawWindows = scheduleFromWeb && cache.Pricing!.Windows.Count > 0
            ? cache.Pricing.Windows
            : c.PeakWindows;
        return new EffectiveConfig
        {
            Windows = ParseWindows(rawWindows),
            RawWindows = rawWindows,
            Holidays = ParseHolidays(c.AutoUpdateEnabled && cache.Holidays != null ? cache.Holidays.Dates : c.Holidays),
            WeekendOffPeak = scheduleFromWeb ? cache.Pricing!.WeekendOffPeak : c.WeekendOffPeak,
            ScheduleFromWeb = scheduleFromWeb,
            HolidaysFromWeb = c.AutoUpdateEnabled && cache.Holidays != null,
            AutoUpdateEnabled = c.AutoUpdateEnabled,
            TimeZone = tz,
            Prices = scheduleFromWeb ? cache.Pricing!.Prices : c.Prices,
            NotifyEnabled = c.NotifyEnabled,
            NotifyLeadMinutes = Math.Max(1, c.NotifyLeadMinutes),
            PricingUrl = c.PricingUrl,
            PricingCache = cache.Pricing,
            HolidayCache = cache.Holidays,
            Raw = c
        };
    }

    /// <summary>悬浮/详情里用的"多久之前"。</summary>
    public static string AgeLabel(long fetchedAt)
    {
        if (fetchedAt <= 0)
        {
            return "未知";
        }
        var age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - fetchedAt);
        var minutes = age / 60000;
        if (minutes < 1) return "刚刚";
        if (minutes < 60) return $"{minutes} 分钟前";
        if (minutes < 1440) return $"{minutes / 60} 小时前";
        return $"{minutes / 1440} 天前";
    }
}
