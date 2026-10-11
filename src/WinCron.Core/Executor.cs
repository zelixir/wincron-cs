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
/// 超时只杀进程本身、启动失败 → ExitCode=-1 + Error=异常消息。
/// </summary>
public static class Executor
{
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
                    // 超时：只杀进程本身（与旧版一致）
                    try { process.Kill(); } catch { /* 可能刚好已退出 */ }
                    try
                    {
                        await process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    catch { /* 等不到就算了 */ }

                    result.ExitCode = -1;
                    result.Error = $"timeout after {job.Timeout}s";
                }

                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                    result.Stdout = await stdoutTask;
                    result.Stderr = await stderrTask;
                }
                catch
                {
                    // 流读取失败不掩盖退出结果
                }
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
}
