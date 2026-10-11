namespace WinCron.Core;

/// <summary>触发来源常量（写入 logs.sqlite 的 trigger_source，SPEC §5）。</summary>
public static class TriggerSources
{
    public const string Cron = "cron";
    public const string Ui = "ui";
    public const string Ipc = "ipc";
    public const string Preview = "preview";
    public const string Hotkey = "hotkey"; // 本版不实现，仅保留常量
}
