using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepSeekPeakStatus;

/// <summary>第二个实例启动时，把已经在运行的主窗口叫到前台。</summary>
internal static class SingleInstance
{
    private const string WindowTitle = "DeepSeek 峰谷时段";

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    public static void TryActivateExisting()
    {
        var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero)
            {
                continue;
            }
            var h = p.MainWindowHandle;
            ShowWindow(h, 9); // SW_RESTORE
            SetForegroundWindow(h);
            return;
        }

        // 主窗口句柄拿不到时（例如被隐藏到托盘），按标题找一次
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h))
            {
                return true;
            }
            var sb = new StringBuilder(256);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString() == WindowTitle)
            {
                ShowWindow(h, 9);
                SetForegroundWindow(h);
                return false;
            }
            return true;
        }, IntPtr.Zero);
    }
}
