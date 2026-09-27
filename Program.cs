using System.Threading;

namespace DeepSeekPeakStatus;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            // 发布前自检：真实联网跑一遍抓取/解析/时段计算，结果打到标准输出
            Environment.ExitCode = SelfTest.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // 单实例：已经在运行时就把主窗口叫到前台，避免同时出现两个托盘图标
        using var mutex = new Mutex(true, @"Local\DeepSeekPeakStatus_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            SingleInstance.TryActivateExisting();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);

        var store = new Store();
        Application.Run(new MainForm(store));
    }
}
