using System.Text;

namespace WinCron.Core;

/// <summary>
/// 应用自身运行日志（app.log），行格式：2026-10-11T09:30:00+08:00 [INFO] ...
/// 超过 5MB 轮转为 app.log.1（直接覆盖旧轮转）。所有写日志异常一律吞掉。
/// </summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 5 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARNING", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Fatal(string message) => Write("FATAL", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                var path = AppPaths.AppLogFile;
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        File.Move(path, path + ".1", overwrite: true);
                    }
                }
                catch
                {
                    // 轮转失败不影响追加
                }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd'T'HH:mm:sszzz} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }
}
