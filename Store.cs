using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekPeakStatus;

public sealed class PriceTier
{
    public string? CacheHit { get; set; }
    public string? CacheMiss { get; set; }
    public string? Output { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(CacheHit) && string.IsNullOrWhiteSpace(CacheMiss) && string.IsNullOrWhiteSpace(Output);
}

public sealed class ModelPrice
{
    public string? Label { get; set; }
    public PriceTier Peak { get; set; } = new();
    public PriceTier OffPeak { get; set; } = new();
}

public sealed class PriceSet
{
    public string Currency { get; set; } = "CNY";
    public string Unit { get; set; } = "元 / 百万 tokens";
    public Dictionary<string, ModelPrice> Models { get; set; } = new();
}

/// <summary>官网拉到、可离线缓存的价格与时段。</summary>
public sealed class PricingCache
{
    public PriceSet Prices { get; set; } = new();
    public List<string> Windows { get; set; } = new();
    public bool WeekendOffPeak { get; set; } = true;
    public string ScheduleText { get; set; } = "";
    public long FetchedAt { get; set; }
    public string Url { get; set; } = "";
}

public sealed class HolidayCache
{
    public List<string> Dates { get; set; } = new();
    public List<int> Years { get; set; } = new();
    public string Source { get; set; } = "";
    public long FetchedAt { get; set; }
    public string Url { get; set; } = "";
}

public sealed class CacheFile
{
    public PricingCache? Pricing { get; set; }
    public HolidayCache? Holidays { get; set; }
    /// <summary>已经提醒过的切换点，避免重启后重复弹窗。</summary>
    public string LastNotified { get; set; } = "";
}

public sealed class AppConfig
{
    public bool AutoUpdateEnabled { get; set; } = true;
    public int PricingIntervalHours { get; set; } = 12;
    public int HolidayIntervalHours { get; set; } = 168;
    public int TimeoutMs { get; set; } = 15000;
    public string TimeZone { get; set; } = "Asia/Shanghai";

    public List<string> PeakWindows { get; set; } = new() { "09:00-12:00", "14:00-18:00" };
    public bool WeekendOffPeak { get; set; } = true;
    public List<string> Holidays { get; set; } = Defaults.Holidays;
    public PriceSet Prices { get; set; } = Defaults.Prices();

    public bool NotifyEnabled { get; set; } = true;
    public int NotifyLeadMinutes { get; set; } = 3;
    public bool StartMinimized { get; set; }
    public string PricingUrl { get; set; } = "https://api-docs.deepseek.com/zh-cn/quick_start/pricing";
    public string HolidayUrlTemplate { get; set; } = "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{year}.json";
}

/// <summary>网络不可用时的兜底数据，取自扩展 1.0.2 的内置默认值。</summary>
public static class Defaults
{
    public static readonly List<string> Holidays = new()
    {
        "2026-01-01", "2026-01-02", "2026-01-03",
        "2026-05-01", "2026-05-02", "2026-05-03", "2026-05-04", "2026-05-05",
        "2026-06-19", "2026-06-20", "2026-06-21",
        "2026-09-25", "2026-09-26", "2026-09-27",
        "2026-10-01", "2026-10-02", "2026-10-03", "2026-10-04",
        "2026-10-05", "2026-10-06", "2026-10-07"
    };

    public static PriceSet Prices() => new()
    {
        Currency = "CNY",
        Unit = "元 / 百万 tokens",
        Models = new Dictionary<string, ModelPrice>
        {
            ["flash"] = new()
            {
                Label = "deepseek-flash",
                Peak = new PriceTier { CacheHit = "0.04", CacheMiss = "2", Output = "8" },
                OffPeak = new PriceTier { CacheHit = "0.02", CacheMiss = "1", Output = "4" }
            },
            ["pro"] = new()
            {
                Label = "deepseek-v4-pro",
                Peak = new PriceTier { CacheHit = "0.30", CacheMiss = "9.0", Output = "27.0" },
                OffPeak = new PriceTier { CacheHit = "0.15", CacheMiss = "4.5", Output = "13.5" }
            }
        }
    };
}

public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>用于判断数据是否变了的紧凑字符串。</summary>
    public static string SerializeForKey<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>配置与缓存的落盘位置：%APPDATA%\DeepSeekPeakStatus\。</summary>
public sealed class Store
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekPeakStatus");

    public string ConfigPath => Path.Combine(DataDir, "config.json");
    public string CachePath => Path.Combine(DataDir, "cache.json");

    public AppConfig Config { get; private set; } = new();
    public CacheFile Cache { get; private set; } = new();

    public Store()
    {
        Load();
    }

    public void Load()
    {
        Config = Read<AppConfig>(ConfigPath) ?? new AppConfig();
        Cache = Read<CacheFile>(CachePath) ?? new CacheFile();
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), AppJson.Options);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            return null;
        }
    }

    public void SaveConfig() => Write(ConfigPath, Config);

    public void SaveCache() => Write(CachePath, Cache);

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(path, JsonSerializer.Serialize(value, AppJson.Options));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }
}
