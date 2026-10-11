namespace WinCron.Core;

/// <summary>
/// 数据目录与全部数据文件的固定路径（与旧 Go 版共用，见 SPEC §2）。
/// </summary>
public static class AppPaths
{
    /// <summary>%ProgramData%\wincron</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "wincron");

    public static string JobsFile => Path.Combine(DataDir, "jobs.json");
    public static string LogsDbFile => Path.Combine(DataDir, "logs.sqlite");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string AppLogFile => Path.Combine(DataDir, "app.log");

    /// <summary>确保数据目录存在。目录不可建（权限等）时返回 false，调用方进入降级模式。</summary>
    public static bool EnsureDataDir()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            return Directory.Exists(DataDir);
        }
        catch
        {
            return false;
        }
    }
}
