using Cronos;

namespace WinCron.Core;

/// <summary>
/// cron 解析工具：5 段标准式（Cronos），"@reboot" 特判为开机任务（SPEC §5）。
/// </summary>
public static class CronUtil
{
    /// <summary>Trim() 后等于 @reboot（忽略大小写）视为开机任务。</summary>
    public static bool IsReboot(string? cron) =>
        !string.IsNullOrWhiteSpace(cron) && cron.Trim().Equals("@reboot", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析失败（含空串、@reboot）返回 null，调用方决定如何处理。</summary>
    public static CronExpression? TryParse(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron) || IsReboot(cron))
        {
            return null;
        }

        try
        {
            // Cronos 0.8.x 中 5 段标准式对应的枚举成员为 CronFormat.Standard（SPEC 所写 StandardFlags 为旧称）
            return CronExpression.Parse(cron.Trim(), CronFormat.Standard);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 下一次触发时间，<b>Kind=Utc</b>（超远期/无下一次时为 null）。
    /// 注意：Cronos 的 GetNextOccurrence(from, zone) 要求 from 必须 Kind=Utc，
    /// 返回值同样是 Kind=Utc —— 展示时须 ToLocalTime()，算 dueTime 用 UtcNow 相减。
    /// </summary>
    public static DateTime? NextUtc(CronExpression expr)
    {
        return expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local);
    }

    /// <summary>下一次触发时间（Kind=Utc）；解析失败或无下一次时返回 null。</summary>
    public static DateTime? NextOccurrence(string cron)
    {
        var expr = TryParse(cron);
        if (expr == null)
        {
            return null;
        }

        try
        {
            return NextUtc(expr);
        }
        catch
        {
            return null;
        }
    }
}
