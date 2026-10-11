using System.Text.Json;
using WinCron.Core;

namespace WinCron.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 单实例（SPEC §7.4）：二次启动尝试 IPC open 把窗口带到前台后退出
        using var mutex = new Mutex(true, @"Local\wincron_cs_single_instance", out var createdNew);
        if (!createdNew)
        {
            AppLog.Info("another instance is running; forwarding 'open' via IPC and exiting");
            IpcClient.TrySend(new IpcRequest { Cmd = "open" }, 2000, out _);
            return 0;
        }

        CrashHandler.Install();
        AppLog.Info($"wincron starting (pid={Environment.ProcessId})");

        var service = new CronService();
        try
        {
            service.Initialize();
        }
        catch (Exception ex)
        {
            // 初始化失败不退出：空任务列表 + 降级模式继续跑
            CrashHandler.WriteCrash(ex, "ERROR");
        }

        // --hidden：不显示主窗口，只留托盘（计划任务看门狗 wincronctl ensure 使用）
        var startHidden = args.Any(a =>
            a is "--hidden" or "-hidden" or "/hidden" or "--minimized" or "-minimized");

        var form = new MainForm(service);
        try
        {
            Application.Run(new AppContext(form, startHidden));
        }
        catch (Exception ex)
        {
            CrashHandler.WriteCrash(ex, "FATAL");
            return 1;
        }
        finally
        {
            try { service.Shutdown(); } catch { }
            AppLog.Info("wincron exited");
        }

        return 0;
    }
}

/// <summary>
/// Application 上下文：支持启动即隐藏（只留托盘）。提前强制创建句柄，
/// 保证隐藏期间 BeginInvoke / IPC open 可用。
/// </summary>
internal sealed class AppContext : ApplicationContext
{
    public AppContext(MainForm form, bool startHidden)
    {
        MainForm = form;
        _ = form.Handle; // 强制创建原生窗口句柄
        if (!startHidden)
        {
            form.Show();
        }
    }
}
