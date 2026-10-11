using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WinCron.Ctl;

/// <summary>
/// wincronctl（SPEC §6）：status/list/enable/disable/run/logs/open/quit/ensure。
/// 退出码：0 成功 / 1 出错 / 2 应用未在运行。
/// </summary>
internal static class Program
{
    private const string PipeName = "wincron_cs_ipc";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions PrettyOpts = new() { WriteIndented = true };

    private sealed class NotRunningException : Exception
    {
        public NotRunningException(string message) : base(message) { }
    }

    private sealed class IpcErrorException : Exception
    {
        public IpcErrorException(string message) : base(message) { }
    }

    private static int Main(string[] args)
    {
        ForceUtf8Output();

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var cmd = args[0].ToLowerInvariant();
        try
        {
            return cmd switch
            {
                "status" => CmdStatus(),
                "list" => CmdList(),
                "enable" => CmdEnableDisable("enable", ArgOrNull(args, 1)),
                "disable" => CmdEnableDisable("disable", ArgOrNull(args, 1)),
                "run" => CmdRun(args),
                "logs" => CmdLogs(args),
                "open" => CmdSimple("open", "window opened"),
                "quit" => CmdSimple("quit", "quit requested"),
                "ensure" => CmdEnsure(),
                "help" or "-h" or "--help" => Help(),
                _ => Unknown(cmd),
            };
        }
        catch (IpcErrorException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (NotRunningException)
        {
            Console.WriteLine("wincron is not running");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        PrintUsage();
        return 1;
    }

    /// <summary>
    /// 任何输出前强制 UTF-8（含用法/错误输出路径）：
    /// 1) Console.OutputEncoding = UTF8 —— 让真实 conhost/Windows Terminal 切到 65001 代码页；
    /// 2) 再把 stdout/stderr 换成显式 UTF-8 写入器 —— Git Bash/mintty 下 stdout 是管道、
    ///    无控制台句柄，第 1 步的赋值可能抛异常被吞并回退系统 GBK 编码产生乱码，
    ///    SetOut/SetError 直接指定编码，保证字节流恒为 UTF-8。
    /// </summary>
    private static void ForceUtf8Output()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 无控制台句柄时可能失败 */ }
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 极端环境保底使用默认输出流 */ }
    }

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static string? ArgOrNull(string[] args, int index) =>
        args.Length > index ? args[index] : null;

    private static void PrintUsage()
    {
        Console.WriteLine("usage: wincronctl <command> [args]");
        Console.WriteLine("  status                 show global status and per-job summary");
        Console.WriteLine("  list                   dump all jobs as JSON");
        Console.WriteLine("  enable [target]        enable globally or by job name / folder");
        Console.WriteLine("  disable [target]       disable globally or by job name / folder");
        Console.WriteLine("  run <name>             run a job now (by name or id)");
        Console.WriteLine("  logs [name] [-n N]     show last N log entries (default 50)");
        Console.WriteLine("  open                   show the main window");
        Console.WriteLine("  quit                   ask wincron to exit gracefully");
        Console.WriteLine("  ensure                 start wincron if not running (Task Scheduler watchdog)");
        Console.WriteLine();
        Console.WriteLine("exit codes: 0 success, 1 error, 2 wincron is not running");
    }

    // ---- IPC ----

    private static JsonElement SendRequest(object request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(3000);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOpts) + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var line = reader.ReadLine();
            if (line == null)
            {
                throw new IOException("empty response from wincron");
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String
                    ? errEl.GetString() ?? "unknown error"
                    : "unknown error";
                throw new IpcErrorException(error);
            }

            return root.TryGetProperty("data", out var dataEl)
                ? dataEl.Clone()
                : JsonSerializer.SerializeToElement(new object());
        }
        catch (IpcErrorException)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw new NotRunningException(ex.Message);
        }
    }

    /// <summary>
    /// 完整探测：连接后发送真实 status 请求并读取响应，能拿到含 ok 字段的应答才算在运行
    /// （不看具体内容，能应答即在运行）。只连不发就断开会在服务端留下 "Pipe is broken" 假错误。
    /// </summary>
    private static bool Probe(int timeoutMs)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd = "status" }, JsonOpts) + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var line = reader.ReadLine();
            if (line == null)
            {
                return false;
            }

            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("ok", out _);
        }
        catch
        {
            return false;
        }
    }

    // ---- 子命令 ----

    private static int CmdStatus()
    {
        var data = SendRequest(new { cmd = "status" });
        var global = data.TryGetProperty("globalEnabled", out var ge) && ge.ValueKind == JsonValueKind.True;
        Console.WriteLine(global ? "调度：运行中" : "调度：已暂停");
        if (data.TryGetProperty("degraded", out var dg) && dg.ValueKind == JsonValueKind.True)
        {
            Console.WriteLine("警告：数据目录不可用，调度已禁用");
        }

        if (data.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
        {
            foreach (var job in jobs.EnumerateArray())
            {
                var name = job.TryGetProperty("name", out var n) ? n.GetString() : "";
                var folder = job.TryGetProperty("folder", out var f) ? f.GetString() : "";
                var enabled = job.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                var failures = job.TryGetProperty("consecutiveFailures", out var cf) && cf.TryGetInt32(out var fi) ? fi : 0;
                var executed = job.TryGetProperty("executedCount", out var ec) && ec.TryGetInt64(out var ei) ? ei : 0;
                var next = job.TryGetProperty("nextRunAt", out var nr) && nr.ValueKind == JsonValueKind.String ? nr.GetString() : "";
                Console.WriteLine($"  {name} [{folder}] {(enabled ? "启用" : "禁用")} 连续失败={failures} 已执行={executed} 下次={PrettyNext(next)}");
            }
        }

        return 0;
    }

    private static string PrettyNext(string? next)
    {
        if (string.IsNullOrEmpty(next))
        {
            return "-";
        }

        if (next == "startup")
        {
            return "开机";
        }

        return DateTimeOffset.TryParse(next, out var dto)
            ? dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : next;
    }

    private static int CmdList()
    {
        var data = SendRequest(new { cmd = "list" });
        if (data.ValueKind != JsonValueKind.Array)
        {
            Console.Error.WriteLine("error: unexpected response");
            return 1;
        }

        foreach (var job in data.EnumerateArray())
        {
            Console.WriteLine(JsonSerializer.Serialize(job, PrettyOpts));
        }

        return 0;
    }

    private static int CmdEnableDisable(string cmd, string? target)
    {
        var data = SendRequest(new { cmd, target });
        var global = data.TryGetProperty("global", out var g) && g.ValueKind == JsonValueKind.True;
        var affected = new List<string>();
        if (data.TryGetProperty("affected", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    affected.Add(item.GetString() ?? "");
                }
            }
        }

        if (global)
        {
            Console.WriteLine($"global scheduling {(cmd == "enable" ? "enabled" : "disabled")} ({affected.Count} job(s) affected)");
        }
        else
        {
            Console.WriteLine($"{cmd}: {string.Join(", ", affected)}");
        }

        return 0;
    }

    private static int CmdRun(string[] args)
    {
        var name = ArgOrNull(args, 1);
        if (string.IsNullOrEmpty(name))
        {
            Console.Error.WriteLine("usage: wincronctl run <name>");
            return 1;
        }

        var data = SendRequest(new { cmd = "run", name });
        var started = data.TryGetProperty("started", out var s) ? s.GetString() : name;
        Console.WriteLine($"started: {started}");
        return 0;
    }

    private static int CmdLogs(string[] args)
    {
        string? name = null;
        var n = 50;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-n" && i + 1 < args.Length)
            {
                _ = int.TryParse(args[++i], out n);
            }
            else if (arg.StartsWith("-n", StringComparison.Ordinal) && arg.Length > 2 && int.TryParse(arg[2..], out var inline))
            {
                n = inline;
            }
            else if (arg.StartsWith('-'))
            {
                // 未知开关忽略
            }
            else if (name == null)
            {
                name = arg;
            }
        }

        if (n <= 0)
        {
            n = 50;
        }

        var data = SendRequest(new { cmd = "logs", name, n });
        if (data.ValueKind != JsonValueKind.Array)
        {
            Console.Error.WriteLine("error: unexpected response");
            return 1;
        }

        foreach (var entry in data.EnumerateArray())
        {
            var started = entry.TryGetProperty("startedAt", out var sa) && sa.TryGetInt64(out var si)
                ? DateTimeOffset.FromUnixTimeMilliseconds(si).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "?";
            var jobName = entry.TryGetProperty("jobName", out var jn) ? jn.GetString() : "";
            var trigger = entry.TryGetProperty("triggerSource", out var ts) ? ts.GetString() : "";
            var running = entry.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True;
            var exitCode = running
                ? "运行中"
                : entry.TryGetProperty("exitCode", out var xc) && xc.TryGetInt32(out var xi) ? xi.ToString() : "?";
            var error = entry.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                ? FirstLine(er.GetString())
                : "";
            Console.WriteLine($"[{started}] {jobName} trigger={trigger} exit={exitCode}{(error.Length > 0 ? " error=" + error : "")}");
        }

        return 0;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var line = text.Split('\n')[0].TrimEnd('\r');
        return line.Length <= 120 ? line : line[..120] + "…";
    }

    private static int CmdSimple(string cmd, string okMessage)
    {
        SendRequest(new { cmd });
        Console.WriteLine(okMessage);
        return 0;
    }

    /// <summary>幂等拉起：通了就报 running；不通则隐藏启动同目录 wincron.exe，等管道就绪（最长 15 秒）。</summary>
    private static int CmdEnsure()
    {
        if (Probe(500))
        {
            Console.WriteLine("wincron is running");
            return 0;
        }

        var exe = Path.Combine(AppContext.BaseDirectory, "wincron.exe");
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"error: wincron.exe not found next to wincronctl: {exe}");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--hidden",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: failed to start wincron: " + ex.Message);
            return 1;
        }

        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(250);
            if (Probe(300))
            {
                Console.WriteLine("wincron started");
                return 0;
            }
        }

        Console.Error.WriteLine("error: wincron pipe did not become ready within 15 seconds");
        return 1;
    }
}
