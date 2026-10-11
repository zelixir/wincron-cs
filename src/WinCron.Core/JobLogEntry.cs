namespace WinCron.Core;

/// <summary>
/// 单次执行记录。结束后作为一条日志写入 logs.sqlite（SPEC §4）；
/// 运行期间仅存在于内存（Running=true），通过 UI/IPC 提供查询。
/// </summary>
public sealed class JobLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    public string JobId { get; set; } = "";

    public string JobName { get; set; } = "";

    public string TriggerSource { get; set; } = "";

    public string CommandLine { get; set; } = "";

    /// <summary>unix 毫秒。</summary>
    public long StartedAt { get; set; }

    /// <summary>unix 毫秒。</summary>
    public long FinishedAt { get; set; }

    public int ExitCode { get; set; }

    public string Stdout { get; set; } = "";

    public string Stderr { get; set; } = "";

    public string Error { get; set; } = "";

    /// <summary>仅内存标志：true 表示仍在运行（不入库）。</summary>
    public bool Running { get; set; }
}
