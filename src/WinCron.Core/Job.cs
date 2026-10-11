using System.Text.Json;

namespace WinCron.Core;

/// <summary>
/// jobs.json 单条任务模型（SPEC §3）。字段一个不多一个不少；camelCase 序列化与旧 Go 版互认。
/// </summary>
public sealed class Job
{
    /// <summary>GUID（小写带连字符）。</summary>
    public string Id { get; set; } = "";

    /// <summary>必填。</summary>
    public string Name { get; set; } = "";

    /// <summary>可空字符串。</summary>
    public string Folder { get; set; } = "";

    /// <summary>标准 5 段式 cron，或 "@reboot"（开机任务）。</summary>
    public string Cron { get; set; } = "";

    /// <summary>必填。</summary>
    public string Command { get; set; } = "";

    /// <summary>可空 → 规范化为空列表。</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>空 = 使用数据目录。</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>"CREATE_NO_WINDOW" 或 ""。</summary>
    public string FlagProcessCreation { get; set; } = "";

    /// <summary>超时秒；0 = 无超时。</summary>
    public int Timeout { get; set; }

    /// <summary>"allow" | "skip" | "kill_old"（空 = "skip"）。</summary>
    public string ConcurrencyPolicy { get; set; } = "skip";

    public bool Enabled { get; set; }

    public bool RunAtStartup { get; set; }

    /// <summary>0 = 不限；连续失败达到该值自动禁用。</summary>
    public int MaxConsecutiveFailures { get; set; }

    public int ConsecutiveFailures { get; set; }

    public long ExecutedCount { get; set; }

    /// <summary>RFC3339 带本地偏移（如 2026-10-10T19:00:00+08:00）；空串 = 未跑过。</summary>
    public string LastExecutedAt { get; set; } = "";

    /// <summary>反序列化后调用：补齐可空字段，保证内存中状态一致。</summary>
    public void Normalize()
    {
        Args ??= new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("D");
        }

        // @reboot 语义上就是开机任务（SPEC §5）
        if (CronUtil.IsReboot(Cron))
        {
            RunAtStartup = true;
        }
    }

    public Job Clone()
    {
        var copy = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(this, Json.Default), Json.Default);
        copy?.Normalize();
        return copy ?? new Job();
    }
}
