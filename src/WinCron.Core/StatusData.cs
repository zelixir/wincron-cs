namespace WinCron.Core;

/// <summary>IPC status 命令的响应数据结构（SPEC §6）。</summary>
public sealed class StatusData
{
    public bool GlobalEnabled { get; set; }

    /// <summary>数据目录不可用的降级标志（附加字段，供诊断）。</summary>
    public bool Degraded { get; set; }

    public List<JobStatusInfo> Jobs { get; set; } = new();
}

public sealed class JobStatusInfo
{
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Enabled { get; set; }
    public int ConsecutiveFailures { get; set; }
    public long ExecutedCount { get; set; }
    public string LastExecutedAt { get; set; } = "";
    /// <summary>下次运行的 RFC3339 本地时间；"" = 未调度；"startup" = 开机任务。</summary>
    public string NextRunAt { get; set; } = "";
}
