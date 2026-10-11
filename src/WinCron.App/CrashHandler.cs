using WinCron.Core;

namespace WinCron.App;

/// <summary>
/// 全局异常处理（SPEC §7.2）：
/// - UI 线程异常：记 crash 日志并尽量继续运行；
/// - AppDomain.UnhandledException（致命）：记日志后 Environment.Exit(1)，依靠看门狗拉起；
/// - UnobservedTaskException：记日志并 SetObserved，进程继续。
/// </summary>
internal static class CrashHandler
{
    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnThreadException(object? sender, ThreadExceptionEventArgs e)
    {
        WriteCrash(e.Exception, "ERROR");
        // 尽量继续运行
    }

    private static void OnDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrash(ex, "FATAL");
        }
        else
        {
            AppLog.Fatal($"unhandled non-exception object: {e.ExceptionObject}");
        }

        Environment.Exit(1); // 依靠看门狗 ≤5 分钟拉起
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrash(e.Exception, "ERROR");
        e.SetObserved();
    }

    /// <summary>写 crash-<时间戳>.log（数据目录不可用时退回 %TEMP%）并记 app.log。</summary>
    public static void WriteCrash(Exception ex, string level)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"time: {DateTimeOffset.Now:yyyy-MM-dd'T'HH:mm:sszzz}");
            sb.AppendLine($"level: {level}");
            Dump(ex, sb, 0);
            var content = sb.ToString();

            var fileName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            var written = false;
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.DataDir, fileName), content);
                written = true;
            }
            catch
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), $"wincron-{fileName}"), content);
                    written = true;
                }
                catch { }
            }

            AppLog.Error($"crash log written={written} file={fileName}");
        }
        catch
        {
            // 连 crash 日志都写不了就只能放弃
        }

        var head = $"{ex.GetType().FullName}: {ex.Message}";
        if (string.Equals(level, "FATAL", StringComparison.Ordinal))
        {
            AppLog.Fatal(head);
        }
        else
        {
            AppLog.Error(head);
        }
    }

    private static void Dump(Exception ex, System.Text.StringBuilder sb, int depth)
    {
        var pad = new string(' ', depth * 2);
        sb.AppendLine($"{pad}type: {ex.GetType().FullName}");
        sb.AppendLine($"{pad}message: {ex.Message}");
        sb.AppendLine($"{pad}stack: {ex.StackTrace ?? "(none)"}");
        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                Dump(inner, sb, depth + 1);
            }
        }
        else if (ex.InnerException != null)
        {
            Dump(ex.InnerException, sb, depth + 1);
        }
    }
}
