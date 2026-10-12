using System.Diagnostics;

namespace WinCron.Core;

/// <summary>单次进程执行结果。</summary>
public sealed class ExecResult
{
    public long StartedAt { get; set; }
    public long FinishedAt { get; set; }
    public int ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>
/// 子进程执行器（对齐 Go 版 execute()，SPEC §5）：UTF-8 捕获 stdout/stderr、
/// 超时杀整棵进程树（2026-10-12 加固）、读管道防挂起（进程退出后最多再等 5 秒，SPEC §5）、
/// 启动失败 → ExitCode=-1 + Error=异常消息。
/// </summary>
public static class Executor
{
    /// <summary>进程退出后等待 stdout/stderr 读任务完成的额外宽限（SPEC §5 读管道防挂起）。</summary>
    private static readonly TimeSpan PipeDrainGrace = TimeSpan.FromSeconds(5);

    public static async Task<ExecResult> ExecuteAsync(Job job, Action<Process>? onStarted = null)
    {
        var result = new ExecResult
        {
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            FileName = job.Command,
            // 空 WorkDir = 数据目录
            WorkingDirectory = string.IsNullOrWhiteSpace(job.WorkDir) ? AppPaths.DataDir : job.WorkDir,
            CreateNoWindow = string.Equals(job.FlagProcessCreation, "CREATE_NO_WINDOW", StringComparison.OrdinalIgnoreCase),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in job.Args ?? new List<string>())
        {
            psi.ArgumentList.Add(arg);
        }

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null)
            {
                result.ExitCode = -1;
                result.Error = "failed to start process";
            }
            else
            {
                onStarted?.Invoke(process);

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource();
                if (job.Timeout > 0)
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(job.Timeout));
                }

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                    result.ExitCode = process.ExitCode;
                }
                catch (OperationCanceledException)
                {
                    // 超时：杀整棵进程树（vbs 包装脚本会派生子进程，只杀父进程会留下持管道的孤儿）
                    KillTree(process);
                    try
                    {
                        await process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    catch { /* 等不到就算了 */ }

                    result.ExitCode = -1;
                    result.Error = $"timeout after {job.Timeout}s";
                }

                // 读管道防挂起（SPEC §5）：进程句柄已退出，但残留子进程可能仍持有管道写端，
                // ReadToEndAsync 永不完成 → 最多再等 5 秒，超时放弃（取已读到的内容），
                // 绝不无限等待卡死整个执行流程。
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(PipeDrainGrace);
                }
                catch
                {
                    // 排空超时（或读取失败）：放弃未完成的读任务，挂异常观察者防 UnobservedTaskException
                    ObserveIfFaulted(stdoutTask);
                    ObserveIfFaulted(stderrTask);
                    AppLog.Info(
                        $"pipe drain abandoned after {PipeDrainGrace.TotalSeconds:0}s "
                        + $"(stdout={(stdoutTask.IsCompleted ? "done" : "pending")}, "
                        + $"stderr={(stderrTask.IsCompleted ? "done" : "pending")}); "
                        + "a leftover child process may still hold the pipe");
                }

                result.Stdout = CompletedResult(stdoutTask);
                result.Stderr = CompletedResult(stderrTask);
            }
        }
        catch (Exception ex)
        {
            // Win32Exception（文件不存在/权限不足等）等启动失败
            result.ExitCode = -1;
            result.Error = ex.Message;
        }
        finally
        {
            result.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try { process?.Dispose(); } catch { }
        }

        return result;
    }

    /// <summary>
    /// 杀整棵进程树（SPEC §5，2026-10-12 加固；超时与 kill_old 共用）：
    /// 优先 <c>taskkill /PID &lt;pid&gt; /T /F</c>，失败兜底 Process.Kill(entireProcessTree: true)。
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
        }
        catch
        {
            // 已 Dispose / 不可查询，无从杀起
            return;
        }

        int pid;
        try
        {
            pid = process.Id;
        }
        catch
        {
            return;
        }

        if (TryTaskKillTree(pid))
        {
            return;
        }

        // 兜底：taskkill 不可用/失败/超时 → .NET 自带整树 Kill
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 可能刚好已退出
        }
    }

    /// <summary>taskkill /PID &lt;pid&gt; /T /F；返回是否确认成功（退出码 0）。整体限时 5 秒。</summary>
    private static bool TryTaskKillTree(int pid)
    {
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (killer == null)
            {
                return false;
            }

            if (!killer.WaitForExit(5000))
            {
                try { killer.Kill(); } catch { }
                return false;
            }

            // taskkill 输出极小（一两行），进程退出后同步读完即返回，不会撑管道
            try
            {
                _ = killer.StandardOutput.ReadToEnd();
                _ = killer.StandardError.ReadToEnd();
            }
            catch { }

            return killer.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读任务已完成（成功结束）才取内容；被放弃/失败一律视为空串。</summary>
    private static string CompletedResult(Task<string> readTask) =>
        readTask.Status == TaskStatus.RanToCompletion ? readTask.Result : "";

    /// <summary>给被放弃的读任务挂异常观察者，避免残留管道读失败变成 UnobservedTaskException。</summary>
    private static void ObserveIfFaulted(Task<string> readTask)
    {
        if (readTask.IsFaulted)
        {
            _ = readTask.Exception; // 已观察
            return;
        }

        if (!readTask.IsCompleted)
        {
            _ = readTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
