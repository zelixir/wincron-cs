using System.Collections.Concurrent;
using System.Diagnostics;
using Cronos;

namespace WinCron.Core;

/// <summary>
/// 调度/执行门面（SPEC §5-§6）。UI 与 IPC 都通过它交互。
/// 事件（JobStarted/JobFinished/JobsChanged/OpenRequested/QuitRequested）可能在后台线程触发，
/// 订阅方需自行 Marshal 到 UI 线程。
/// </summary>
public sealed class CronService : IDisposable
{
    private readonly JobsStore _jobsStore = new();
    private readonly StateStore _stateStore = new();
    private readonly SqliteLogStore _logStore;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, TimerEntry> _timers = new();
    private readonly ConcurrentDictionary<string, JobLogEntry> _running = new();
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    private readonly ConcurrentDictionary<string, object> _startLocks = new();
    private readonly List<Job> _jobs = new();
    private GlobalState _state = new();
    private IpcServer? _ipc;
    private HttpRpcServer? _httpRpc;
    private int _generation;
    private bool _degraded;
    private bool _disposed;

    public CronService()
    {
        _logStore = new SqliteLogStore(AppPaths.LogsDbFile);
    }

    // ---- 事件（UI 订阅） ----

    public event Action<JobLogEntry>? JobStarted;
    public event Action<JobLogEntry>? JobFinished;
    public event Action? JobsChanged;
    public event Action? OpenRequested;
    public event Action? QuitRequested;

    // ---- 状态 ----

    /// <summary>数据目录不可建（权限）等降级：进程存活但调度禁用。</summary>
    public bool Degraded => _degraded;

    public bool GlobalEnabled
    {
        get { lock (_lock) { return _state.GlobalEnabled; } }
    }

    // ---- 生命周期 ----

    public void Initialize()
    {
        _degraded = !AppPaths.EnsureDataDir();
        if (_degraded)
        {
            AppLog.Error($"data directory unavailable (scheduling disabled): {AppPaths.DataDir}");
        }

        AppLog.Info($"wincron service initializing, data dir: {AppPaths.DataDir}{(_degraded ? " (unavailable)" : "")}");
        _logStore.Open();

        lock (_lock)
        {
            _jobs.Clear();
            _jobs.AddRange(_jobsStore.Load());
            _state = _stateStore.Load();
        }

        AppLog.Info($"loaded {_jobs.Count} job(s), globalEnabled={_state.GlobalEnabled}");

        // IpcServer 构造时即在后台启动监听循环
        _ipc = new IpcServer(HandleIpc, () => QuitRequested?.Invoke());

        // HTTP RPC（SPEC §12）：rpcPort=0 关闭，缺省 17877；共用 HandleIpc 分发器
        StartHttpRpc();

        // SPEC §5 启动顺序：加载 jobs.json → 启用且 RunAtStartup 的任务异步执行一轮 → 其余建定时器
        RunStartupJobs();
        RebuildTimers();
    }

    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _httpRpc?.Stop(); } catch { }
        try { _ipc?.Stop(); } catch { }
        foreach (var timer in _timers.Values)
        {
            try { timer.Timer.Dispose(); } catch { }
        }

        _timers.Clear();
        lock (_lock)
        {
            try { _jobsStore.Save(_jobs); } catch { }
            try { _stateStore.Save(_state); } catch { }
        }

        try { _logStore.Dispose(); } catch { }
        AppLog.Info("wincron service stopped");
    }

    public void Dispose() => Shutdown();

    // ---- 任务查询（UI / IPC） ----

    public List<Job> GetJobs()
    {
        lock (_lock)
        {
            return _jobs.ToList();
        }
    }

    public Job? FindJob(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }

        lock (_lock)
        {
            return _jobs.FirstOrDefault(j =>
                string.Equals(j.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>UI 显示用：下次运行文本。"" = 未调度；"startup" = 开机任务；否则 RFC3339 本地时间。</summary>
    public string GetNextRunText(Job job)
    {
        lock (_lock)
        {
            return NextRunTextLocked(job);
        }
    }

    private string NextRunTextLocked(Job job)
    {
        if (IsStartupJob(job))
        {
            return "startup";
        }

        if (!job.Enabled || !_state.GlobalEnabled)
        {
            return "";
        }

        var expr = CronUtil.TryParse(job.Cron);
        if (expr == null)
        {
            return "";
        }

        try
        {
            var nextUtc = CronUtil.NextUtc(expr);
            return nextUtc.HasValue
                ? nextUtc.Value.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz")
                : "";
        }
        catch
        {
            return "";
        }
    }

    public StatusData GetStatus()
    {
        lock (_lock)
        {
            return new StatusData
            {
                GlobalEnabled = _state.GlobalEnabled,
                Degraded = _degraded,
                Jobs = _jobs.Select(j => new JobStatusInfo
                {
                    Name = j.Name,
                    Folder = j.Folder,
                    Enabled = j.Enabled,
                    ConsecutiveFailures = j.ConsecutiveFailures,
                    ExecutedCount = j.ExecutedCount,
                    LastExecutedAt = j.LastExecutedAt,
                    NextRunAt = NextRunTextLocked(j),
                }).ToList(),
            };
        }
    }

    // ---- 任务变更（UI / IPC 共用） ----

    public void AddJob(Job job)
    {
        job.Normalize();
        lock (_lock)
        {
            _jobs.Add(job);
            SaveJobsLocked();
        }

        AppLog.Info($"job '{job.Name}' created ({job.Id})");
        RebuildTimers();
        RaiseJobsChanged();
    }

    public void UpdateJob(Job updated)
    {
        updated.Normalize();
        lock (_lock)
        {
            var idx = _jobs.FindIndex(j => string.Equals(j.Id, updated.Id, StringComparison.Ordinal));
            if (idx < 0)
            {
                throw new InvalidOperationException($"job '{updated.Name}' not found");
            }

            _jobs[idx] = updated;
            SaveJobsLocked();
        }

        AppLog.Info($"job '{updated.Name}' updated ({updated.Id})");
        RebuildTimers();
        RaiseJobsChanged();
    }

    public bool DeleteJob(string id)
    {
        Job? removed;
        lock (_lock)
        {
            removed = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
            if (removed == null)
            {
                return false;
            }

            _jobs.Remove(removed);
            if (_timers.TryRemove(id, out var timer))
            {
                try { timer.Timer.Dispose(); } catch { }
            }

            SaveJobsLocked();
        }

        AppLog.Info($"job '{removed.Name}' deleted ({removed.Id})");
        RebuildTimers();
        RaiseJobsChanged();
        return true;
    }

    public void SetJobEnabled(string id, bool enabled)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.Ordinal));
            if (job == null || job.Enabled == enabled)
            {
                return;
            }

            job.Enabled = enabled;
            SaveJobsLocked();
            AppLog.Info($"job '{job.Name}' {(enabled ? "enabled" : "disabled")}");
        }

        RebuildTimers();
        RaiseJobsChanged();
    }

    public void SetGlobalEnabled(bool enabled)
    {
        lock (_lock)
        {
            _state.GlobalEnabled = enabled;
            try
            {
                _stateStore.Save(_state);
            }
            catch (Exception ex)
            {
                AppLog.Error($"save state.json failed: {ex.Message}");
            }

            AppLog.Info($"global scheduling {(enabled ? "resumed" : "paused")}");
        }

        RebuildTimers();
        RaiseJobsChanged();
    }

    /// <summary>从磁盘重读 jobs.json（UI 刷新按钮用，可拾取外部修改）。</summary>
    public void ReloadJobsFromDisk()
    {
        lock (_lock)
        {
            _jobs.Clear();
            _jobs.AddRange(_jobsStore.Load());
        }

        AppLog.Info("jobs.json reloaded from disk");
        RebuildTimers();
        RaiseJobsChanged();
    }

    // ---- 运行与日志 ----

    /// <summary>立即运行（UI/IPC/开机）。返回 (false, error) 表示未启动（并发策略/找不到等）。</summary>
    public (bool Ok, string Error) StartJob(Job job, string trigger)
    {
        if (_disposed)
        {
            return (false, "service is shutting down");
        }

        var policy = NormalizePolicy(job.ConcurrencyPolicy);
        var startLock = _startLocks.GetOrAdd(job.Id, _ => new object());
        JobLogEntry entry;
        lock (startLock)
        {
            var running = GetRunning(job.Id);
            if (running.Count > 0)
            {
                switch (policy)
                {
                    case "skip":
                        if (string.Equals(trigger, TriggerSources.Cron, StringComparison.Ordinal))
                        {
                            return (true, ""); // cron 触发静默跳过
                        }

                        AppLog.Info($"job '{job.Name}' trigger={trigger} skipped: already running");
                        return (false, "job is already running");

                    case "kill_old":
                        AppLog.Info($"job '{job.Name}' kill_old: killing {running.Count} running instance(s)");
                        foreach (var r in running)
                        {
                            if (_processes.TryGetValue(r.Id, out var p))
                            {
                                try { p.Kill(); } catch { }
                            }
                        }

                        foreach (var r in running)
                        {
                            if (_processes.TryGetValue(r.Id, out var p))
                            {
                                try { p.WaitForExit(2000); } catch { }
                            }
                        }

                        break;

                    // allow：可并行，什么都不做
                }
            }

            entry = new JobLogEntry
            {
                JobId = job.Id,
                JobName = job.Name,
                TriggerSource = trigger,
                CommandLine = BuildCommandLine(job),
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Running = true,
            };
            _running[entry.Id] = entry;
            _logStore.TrackRunning(entry);
        }

        AppLog.Info($"job '{job.Name}' started (trigger={trigger})");
        RaiseJobStarted(entry);
        _ = Task.Run(() => ExecuteCoreAsync(job, entry));
        return (true, "");
    }

    /// <summary>按名称或 id 找任务并立即执行。</summary>
    public (bool Ok, string Error) RunJobByNameOrId(string key, string trigger)
    {
        var job = FindJob(key);
        if (job == null)
        {
            return (false, $"job '{key}' not found");
        }

        return StartJob(job, trigger);
    }

    private async Task ExecuteCoreAsync(Job job, JobLogEntry entry)
    {
        ExecResult result;
        try
        {
            result = await Executor.ExecuteAsync(job, p =>
            {
                if (p != null)
                {
                    _processes[entry.Id] = p;
                }
            });
        }
        catch (Exception ex)
        {
            result = new ExecResult
            {
                StartedAt = entry.StartedAt,
                FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExitCode = -1,
                Error = ex.Message,
            };
        }
        finally
        {
            _processes.TryRemove(entry.Id, out _);
        }

        entry.Running = false;
        entry.ExitCode = result.ExitCode;
        entry.Stdout = result.Stdout ?? "";
        entry.Stderr = result.Stderr ?? "";
        entry.Error = result.Error ?? "";
        entry.FinishedAt = result.FinishedAt;
        _logStore.UntrackRunning(entry.Id);
        _logStore.InsertFinished(entry);

        var elapsedMs = Math.Max(0, entry.FinishedAt - entry.StartedAt);
        AppLog.Info(
            $"job '{entry.JobName}' finished (trigger={entry.TriggerSource}) exitCode={entry.ExitCode} elapsed={elapsedMs}ms"
            + (entry.Error.Length > 0 ? $" error={entry.Error}" : ""));

        var failed = entry.ExitCode != 0 || !string.IsNullOrEmpty(entry.Error);
        var autoDisabled = false;
        lock (_lock)
        {
            var current = _jobs.FirstOrDefault(x => string.Equals(x.Id, entry.JobId, StringComparison.Ordinal));
            if (current != null)
            {
                current.ExecutedCount++;
                current.LastExecutedAt = FormatLocal(entry.FinishedAt);
                if (!failed)
                {
                    current.ConsecutiveFailures = 0;
                }
                else
                {
                    current.ConsecutiveFailures++;
                }

                if (current.Enabled && current.MaxConsecutiveFailures > 0
                    && current.ConsecutiveFailures >= current.MaxConsecutiveFailures)
                {
                    current.Enabled = false;
                    autoDisabled = true;
                    if (_timers.TryRemove(current.Id, out var timer))
                    {
                        try { timer.Timer.Dispose(); } catch { }
                    }
                }

                try
                {
                    _jobsStore.Save(_jobs);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"save jobs.json failed: {ex.Message}");
                }
            }
        }

        if (autoDisabled)
        {
            AppLog.Warning($"job '{entry.JobName}' automatically disabled after {entry.Error} consecutive failures reached the limit");
        }

        RaiseJobFinished(entry);
        RaiseJobsChanged();
    }

    public List<JobLogEntry> GetLogs(string? jobNameOrId, int n)
    {
        string? jobId = null;
        string? jobName = null;
        if (!string.IsNullOrWhiteSpace(jobNameOrId))
        {
            var job = FindJob(jobNameOrId);
            if (job != null)
            {
                jobId = job.Id;
                jobName = job.Name;
            }
            else
            {
                jobName = jobNameOrId; // 可能是历史名（任务已改名/删除）
            }
        }

        return _logStore.Query(jobId, jobName, n);
    }

    /// <summary>jobNameOrId 为空 → 清空全部日志。</summary>
    public void ClearLogs(string? jobNameOrId)
    {
        if (string.IsNullOrWhiteSpace(jobNameOrId))
        {
            _logStore.ClearAll();
            AppLog.Info("all job logs cleared");
        }
        else
        {
            var job = FindJob(jobNameOrId);
            _logStore.Clear(job?.Id, jobNameOrId);
            AppLog.Info($"logs cleared for job '{jobNameOrId}'");
        }
    }

    // ---- HTTP RPC（SPEC §12） ----

    private void StartHttpRpc()
    {
        if (_state.RpcPort <= 0)
        {
            AppLog.Info("http rpc disabled (rpcPort=0)");
            return;
        }

        try
        {
            _httpRpc = new HttpRpcServer(HandleIpc, _state.RpcPort);
            _httpRpc.Start(); // 成功后内部记 [INFO] http rpc listening on 127.0.0.1:<port>
        }
        catch (Exception ex)
        {
            // 端口被占等启动失败：记 ERROR，不影响调度主流程
            AppLog.Error($"http rpc start failed: {ex.Message}");
            try { _httpRpc?.Stop(); } catch { }
            _httpRpc = null;
        }
    }

    // ---- 调度器 ----

    private sealed class TimerEntry
    {
        public System.Threading.Timer Timer = null!;
        public int Generation;
    }

    private void RunStartupJobs()
    {
        List<Job> snapshot;
        lock (_lock)
        {
            snapshot = _jobs.Where(j => j.Enabled && IsStartupJob(j)).ToList();
        }

        if (snapshot.Count == 0)
        {
            return;
        }

        AppLog.Info($"startup trigger fired for {snapshot.Count} job(s)");
        foreach (var job in snapshot)
        {
            var j = job;
            _ = Task.Run(() =>
            {
                try
                {
                    StartJob(j, TriggerSources.Cron);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"startup run of '{j.Name}' failed: {ex.Message}");
                }
            });
        }
    }

    private void RebuildTimers()
    {
        List<TimerEntry> old;
        lock (_lock)
        {
            old = _timers.Values.ToList();
            _timers.Clear();
            _generation++;
            if (_state.GlobalEnabled)
            {
                foreach (var job in _jobs)
                {
                    if (!job.Enabled || IsStartupJob(job))
                    {
                        continue;
                    }

                    var expr = CronUtil.TryParse(job.Cron);
                    if (expr == null)
                    {
                        AppLog.Error($"job '{job.Name}' cron '{job.Cron}' cannot be parsed; not scheduled");
                        continue;
                    }

                    var due = ComputeDue(expr);
                    if (due == Timeout.InfiniteTimeSpan)
                    {
                        AppLog.Warning($"job '{job.Name}' has no next occurrence for cron '{job.Cron}'; not scheduled");
                        continue;
                    }

                    var entry = new TimerEntry { Generation = _generation };
                    entry.Timer = new System.Threading.Timer(
                        _ => OnTimerFired(job.Id, entry), null, due, Timeout.InfiniteTimeSpan);
                    _timers[job.Id] = entry;
                }
            }
        }

        foreach (var timer in old)
        {
            try { timer.Timer.Dispose(); } catch { }
        }
    }

    private void OnTimerFired(string jobId, TimerEntry entry)
    {
        if (entry.Generation != Volatile.Read(ref _generation))
        {
            return; // 已被重建淘汰
        }

        Job? job;
        lock (_lock)
        {
            if (!_state.GlobalEnabled)
            {
                return;
            }

            job = _jobs.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.Ordinal));
            if (job == null || !job.Enabled || IsStartupJob(job))
            {
                return;
            }
        }

        // 触发后立即重算下次（到点已过期会立即触发，即补跑唤醒后错过的点）
        var expr = CronUtil.TryParse(job.Cron);
        if (expr != null)
        {
            try
            {
                entry.Timer.Change(ComputeDue(expr), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        StartJob(job, TriggerSources.Cron);
    }

    private static TimeSpan ComputeDue(CronExpression expr)
    {
        try
        {
            // Cronos 要求 from 为 Kind=Utc，返回值也是 Kind=Utc
            var nextUtc = CronUtil.NextUtc(expr);
            if (!nextUtc.HasValue)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var due = nextUtc.Value - DateTime.UtcNow;
            return due <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50) : due;
        }
        catch
        {
            return Timeout.InfiniteTimeSpan;
        }
    }

    // ---- IPC 分发（SPEC §6 服务端命令） ----

    public IpcResponse HandleIpc(IpcRequest req)
    {
        try
        {
            switch ((req.Cmd ?? "").Trim().ToLowerInvariant())
            {
                case "status":
                    return IpcResponse.Success(GetStatus());

                case "list":
                    return IpcResponse.Success(GetJobs());

                case "enable":
                    return IpcResponse.Success(SetEnabledInternal(req.Target, true));

                case "disable":
                    return IpcResponse.Success(SetEnabledInternal(req.Target, false));

                case "run":
                {
                    if (string.IsNullOrWhiteSpace(req.Name))
                    {
                        return IpcResponse.Fail("run: missing job name");
                    }

                    var job = FindJob(req.Name);
                    if (job == null)
                    {
                        return IpcResponse.Fail($"job '{req.Name}' not found");
                    }

                    var (ok, err) = StartJob(job, TriggerSources.Ipc);
                    return ok ? IpcResponse.Success(new { started = job.Name }) : IpcResponse.Fail(err);
                }

                case "logs":
                    return IpcResponse.Success(GetLogs(req.Name, req.N > 0 ? req.N : 50));

                case "open":
                    OpenRequested?.Invoke();
                    return IpcResponse.Success(new { opened = true });

                case "quit":
                    // 实际退出由 IpcServer 在响应送达并断开后触发 onQuit
                    return IpcResponse.Success(new { quitting = true });

                default:
                    return IpcResponse.Fail($"unknown command '{req.Cmd}'");
            }
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    /// <summary>enable/disable：target 空 = 全局开关；否则先按任务名（忽略大小写）精确匹配，再按 folder 匹配。</summary>
    private object SetEnabledInternal(string? target, bool enable)
    {
        var affected = new List<string>();
        bool global;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                global = true;
                if (_state.GlobalEnabled != enable)
                {
                    _state.GlobalEnabled = enable;
                    try
                    {
                        _stateStore.Save(_state);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error($"save state.json failed: {ex.Message}");
                    }

                    AppLog.Info($"global scheduling {(enable ? "enabled" : "disabled")} via ipc");
                }

                affected.AddRange(_jobs.Select(j => j.Name));
            }
            else
            {
                global = false;
                var byName = _jobs.Where(j => string.Equals(j.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
                var matched = byName.Count > 0
                    ? byName
                    : _jobs.Where(j => string.Equals(j.Folder, target, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matched.Count == 0)
                {
                    throw new InvalidOperationException($"no job or folder matched '{target}'");
                }

                foreach (var j in matched)
                {
                    if (j.Enabled != enable)
                    {
                        j.Enabled = enable;
                        AppLog.Info($"job '{j.Name}' {(enable ? "enabled" : "disabled")} via ipc");
                    }

                    affected.Add(j.Name);
                }

                SaveJobsLocked();
            }
        }

        RebuildTimers();
        RaiseJobsChanged();
        return new { global, affected };
    }

    // ---- 辅助 ----

    private bool IsStartupJob(Job job) => job.RunAtStartup || CronUtil.IsReboot(job.Cron);

    private List<JobLogEntry> GetRunning(string jobId) =>
        _running.Values.Where(r => string.Equals(r.JobId, jobId, StringComparison.Ordinal)).ToList();

    private void SaveJobsLocked()
    {
        try
        {
            _jobsStore.Save(_jobs);
        }
        catch (Exception ex)
        {
            AppLog.Error($"save jobs.json failed: {ex.Message}");
        }
    }

    private static string NormalizePolicy(string? policy)
    {
        var v = (policy ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "allow" => "allow",
            "kill_old" => "kill_old",
            _ => "skip", // 空/未知一律按 skip
        };
    }

    private static string BuildCommandLine(Job job)
    {
        var parts = new List<string> { job.Command };
        parts.AddRange(job.Args ?? new List<string>());
        return string.Join(" ", parts);
    }

    private static string FormatLocal(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    private void RaiseJobStarted(JobLogEntry entry)
    {
        try { JobStarted?.Invoke(entry); }
        catch (Exception ex) { AppLog.Error($"JobStarted handler failed: {ex.Message}"); }
    }

    private void RaiseJobFinished(JobLogEntry entry)
    {
        try { JobFinished?.Invoke(entry); }
        catch (Exception ex) { AppLog.Error($"JobFinished handler failed: {ex.Message}"); }
    }

    private void RaiseJobsChanged()
    {
        try { JobsChanged?.Invoke(); }
        catch (Exception ex) { AppLog.Error($"JobsChanged handler failed: {ex.Message}"); }
    }
}
