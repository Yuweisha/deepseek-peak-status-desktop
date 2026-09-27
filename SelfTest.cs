namespace DeepSeekPeakStatus;

/// <summary>
/// 命令行自检（exe --selftest）：联网跑一遍真实的抓取与解析，把结果打到标准输出。
/// 用于发布前验证解析规则仍然匹配官网结构，不涉及界面。
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var store = new Store();
        var cfg = store.Config;
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 没有控制台时忽略
        }
        Console.WriteLine($"数据目录：{Store.DataDir}");
        Console.WriteLine($"时区：{Peak.ResolveTimeZone(cfg.TimeZone).Id}");
        Console.WriteLine();

        var result = await Updater.RefreshAsync(store, true);
        Console.WriteLine(result.Ok ? "刷新：成功" : "刷新：部分失败 → " + string.Join("；", result.Problems));

        var pricing = store.Cache.Pricing;
        if (pricing != null)
        {
            Console.WriteLine($"价格/时段来源：{pricing.Url}");
            Console.WriteLine($"峰时区间：{string.Join("、", pricing.Windows)}（周末全天空闲={pricing.WeekendOffPeak}）");
            Console.WriteLine($"脚注原文：{pricing.ScheduleText}");
            foreach (var (id, m) in pricing.Prices.Models)
            {
                Console.WriteLine($"  {m.Label ?? id}: 峰 命中{m.Peak.CacheHit} 未命中{m.Peak.CacheMiss} 输出{m.Peak.Output} " +
                                  $"| 空闲 命中{m.OffPeak.CacheHit} 未命中{m.OffPeak.CacheMiss} 输出{m.OffPeak.Output}");
            }
        }

        var holiday = store.Cache.Holidays;
        if (holiday != null)
        {
            Console.WriteLine($"节假日来源：{holiday.Url}（{holiday.Dates.Count} 天，{holiday.Source}）");
            Console.WriteLine("  " + string.Join(" ", holiday.Dates.Take(40)));
        }

        Console.WriteLine();
        var effective = Peak.Resolve(store.Config, store.Cache);
        var snap = Peak.Build(effective);
        Console.WriteLine($"现在：{snap.DateKey} {Peak.WeekdayName(snap.Weekday)} {snap.Minutes / 60:D2}:{snap.Minutes % 60:D2}");
        Console.WriteLine($"档位：{snap.TierName}（{snap.DayKind}{(snap.AllDayOff ? "，全天按空闲价计费" : "")}）");
        Console.WriteLine($"下次切换：{snap.NextLabel} → {(snap.NextPeak == null ? "--" : snap.NextPeak.Value ? "峰时" : "空闲")}" +
                          (snap.Remaining != null ? $"，剩 {Peak.FormatRemaining(snap.Remaining.Value)}" : ""));
        Console.WriteLine("今日时段：");
        foreach (var seg in snap.Segments)
        {
            Console.WriteLine($"  {Peak.FormatMinute(seg.Start)} - {Peak.FormatMinute(seg.End)}  {(seg.Peak ? "峰时" : "空闲")}" +
                              (seg == snap.Current ? "  ← 当前" : ""));
        }

        // 离线兜底：把缓存清掉再算一次，确认内置默认值也能给出合理结果
        var offline = Peak.Resolve(new AppConfig(), new CacheFile());
        var offlineSnap = Peak.Build(offline);
        Console.WriteLine();
        Console.WriteLine($"离线兜底：{offlineSnap.TierName} / {offlineSnap.DayKind} / 区间 {string.Join("、", offline.RawWindows)} / 节假日 {offline.Holidays.Count} 天");

        return result.Ok ? 0 : 1;
    }
}
