using System.Text;

namespace DeepSeekPeakStatus;

/// <summary>把异常写进数据目录，方便用户反馈问题时贴日志。</summary>
internal static class ErrorLog
{
    public static void Write(Exception ex)
    {
        try
        {
            var path = Path.Combine(Store.DataDir, "error.log");
            Directory.CreateDirectory(Store.DataDir);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // 记日志失败就算了，不能让它再抛一次
        }
    }
}
