using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace WinCron.Core;

/// <summary>
/// logs.sqlite 访问（与旧版同结构，SPEC §4）。打开失败重试 3 次后退化为 :memory:。
/// 运行中的条目仅在内存里（TrackRunning/UntrackRunning），结束时插入一条。
/// </summary>
public sealed class SqliteLogStore : IDisposable
{
    /// <summary>stdout/stderr 入库截断长度。</summary>
    public const int TextLimit = 16384;

    private const string SchemaJobs = """
        CREATE TABLE IF NOT EXISTS job_logs (
          id TEXT PRIMARY KEY,
          job_id TEXT NOT NULL,
          job_name TEXT NOT NULL,
          trigger_source TEXT NOT NULL DEFAULT '',
          command_line TEXT NOT NULL,
          started_at INTEGER NOT NULL,
          finished_at INTEGER NOT NULL,
          exit_code INTEGER NOT NULL,
          stdout TEXT NOT NULL,
          stderr TEXT NOT NULL,
          error TEXT NOT NULL
        )
        """;

    private const string SchemaIndex =
        "CREATE INDEX IF NOT EXISTS idx_job_logs_job_id_started_at ON job_logs(job_id, started_at DESC)";

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, JobLogEntry> _running = new();
    private SqliteConnection? _conn;
    private bool _memoryFallback;

    public SqliteLogStore(string dbPath)
    {
        _path = dbPath;
    }

    public bool IsMemoryFallback => _memoryFallback;

    public void Open()
    {
        // 数据目录都不存在（降级模式）就直接走内存，不做无谓重试
        var dir = Path.GetDirectoryName(_path);
        var dirOk = string.IsNullOrEmpty(dir) || Directory.Exists(dir);

        if (dirOk)
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    OpenAndPrepare(_path);
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"logs.sqlite open failed (attempt {attempt}/3): {ex.Message}");
                    Thread.Sleep(300);
                }
            }
        }

        try
        {
            OpenAndPrepare(":memory:");
            _memoryFallback = true;
            AppLog.Error("logs.sqlite unavailable; fell back to in-memory log store (history unavailable until restart)");
        }
        catch (Exception ex)
        {
            AppLog.Error($"in-memory log store init failed: {ex.Message}; log persistence disabled");
            _conn = null;
        }
    }

    private void OpenAndPrepare(string dataSource)
    {
        SqliteConnection conn;
        if (dataSource == ":memory:")
        {
            conn = new SqliteConnection("Data Source=:memory:");
        }
        else
        {
            conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dataSource,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
        }

        try
        {
            conn.Open();
            Exec(conn, "PRAGMA busy_timeout=3000");
            Exec(conn, "PRAGMA journal_mode=WAL");
            Exec(conn, "PRAGMA synchronous=NORMAL");
            Exec(conn, SchemaJobs);
            Exec(conn, SchemaIndex);
        }
        catch
        {
            try { conn.Dispose(); } catch { }
            throw;
        }

        _conn?.Dispose();
        _conn = conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- 运行中条目（仅内存） ----

    public void TrackRunning(JobLogEntry entry) => _running[entry.Id] = entry;

    public void UntrackRunning(string entryId) => _running.TryRemove(entryId, out _);

    // ---- 结束条目入库 ----

    public void InsertFinished(JobLogEntry entry)
    {
        if (_conn == null)
        {
            return;
        }

        try
        {
            _gate.Wait();
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO job_logs
                      (id, job_id, job_name, trigger_source, command_line, started_at, finished_at, exit_code, stdout, stderr, error)
                    VALUES
                      ($id, $job_id, $job_name, $trigger_source, $command_line, $started_at, $finished_at, $exit_code, $stdout, $stderr, $error)
                    """;
                cmd.Parameters.AddWithValue("$id", entry.Id);
                cmd.Parameters.AddWithValue("$job_id", entry.JobId);
                cmd.Parameters.AddWithValue("$job_name", entry.JobName);
                cmd.Parameters.AddWithValue("$trigger_source", entry.TriggerSource);
                cmd.Parameters.AddWithValue("$command_line", entry.CommandLine);
                cmd.Parameters.AddWithValue("$started_at", entry.StartedAt);
                cmd.Parameters.AddWithValue("$finished_at", entry.FinishedAt);
                cmd.Parameters.AddWithValue("$exit_code", entry.ExitCode);
                cmd.Parameters.AddWithValue("$stdout", Cut(entry.Stdout));
                cmd.Parameters.AddWithValue("$stderr", Cut(entry.Stderr));
                cmd.Parameters.AddWithValue("$error", Cut(entry.Error));
                cmd.ExecuteNonQuery();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"insert job log failed: {ex.Message}");
        }
    }

    /// <summary>
    /// tail 查询（新的在前），合并运行中的内存条目。jobId/jobName 均为 null 时查全部。
    /// </summary>
    public List<JobLogEntry> Query(string? jobId, string? jobName, int limit)
    {
        if (limit <= 0)
        {
            limit = 50;
        }

        var result = new List<JobLogEntry>();
        if (_conn != null)
        {
            try
            {
                _gate.Wait();
                try
                {
                    using var cmd = _conn.CreateCommand();
                    var where = new List<string>();
                    if (jobId != null)
                    {
                        where.Add("job_id = $job_id");
                        cmd.Parameters.AddWithValue("$job_id", jobId);
                    }

                    if (jobName != null)
                    {
                        where.Add("job_name = $job_name");
                        cmd.Parameters.AddWithValue("$job_name", jobName);
                    }

                    cmd.Parameters.AddWithValue("$n", limit);
                    cmd.CommandText =
                        "SELECT id, job_id, job_name, trigger_source, command_line, started_at, finished_at, exit_code, stdout, stderr, error FROM job_logs"
                        + (where.Count > 0 ? " WHERE " + string.Join(" OR ", where) : "")
                        + " ORDER BY started_at DESC LIMIT $n";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add(new JobLogEntry
                        {
                            Id = reader.GetString(0),
                            JobId = reader.GetString(1),
                            JobName = reader.GetString(2),
                            TriggerSource = reader.GetString(3),
                            CommandLine = reader.GetString(4),
                            StartedAt = reader.GetInt64(5),
                            FinishedAt = reader.GetInt64(6),
                            ExitCode = reader.GetInt32(7),
                            Stdout = reader.IsDBNull(8) ? "" : reader.GetString(8),
                            Stderr = reader.IsDBNull(9) ? "" : reader.GetString(9),
                            Error = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            Running = false,
                        });
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error($"query job logs failed: {ex.Message}");
            }
        }

        foreach (var r in _running.Values)
        {
            var match = (jobId == null && jobName == null)
                        || (jobId != null && string.Equals(r.JobId, jobId, StringComparison.Ordinal))
                        || (jobName != null && string.Equals(r.JobName, jobName, StringComparison.Ordinal));
            if (match)
            {
                result.Add(r);
            }
        }

        return result.OrderByDescending(e => e.StartedAt).Take(limit).ToList();
    }

    /// <summary>清空指定任务的日志（jobId / jobName 任一匹配即删）；两者皆空时清全部。</summary>
    public void Clear(string? jobId, string? jobName)
    {
        if (_conn == null)
        {
            return;
        }

        try
        {
            _gate.Wait();
            try
            {
                using var cmd = _conn.CreateCommand();
                var where = new List<string>();
                if (jobId != null)
                {
                    where.Add("job_id = $job_id");
                    cmd.Parameters.AddWithValue("$job_id", jobId);
                }

                if (jobName != null)
                {
                    where.Add("job_name = $job_name");
                    cmd.Parameters.AddWithValue("$job_name", jobName);
                }

                cmd.CommandText = "DELETE FROM job_logs" + (where.Count > 0 ? " WHERE " + string.Join(" OR ", where) : "");
                cmd.ExecuteNonQuery();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"clear job logs failed: {ex.Message}");
        }
    }

    public void ClearAll() => Clear(null, null);

    private static string Cut(string? s)
    {
        s ??= "";
        return s.Length <= TextLimit ? s : s[..TextLimit];
    }

    public void Dispose()
    {
        try { _conn?.Dispose(); } catch { }
        _conn = null;
        _gate.Dispose();
    }
}
