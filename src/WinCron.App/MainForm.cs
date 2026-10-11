using System.Globalization;
using System.Text;
using WinCron.Core;

namespace WinCron.App;

/// <summary>
/// 主窗口（SPEC §8）：顶部状态条 + 任务/日志两个 Tab；关闭=隐藏到托盘，退出仅经托盘菜单或 IPC quit。
/// </summary>
public sealed class MainForm : Form
{
    private readonly CronService _service;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly Dictionary<string, DateTime> _lastFailBalloon = new();
    private Dictionary<string, Job> _jobsById = new();
    private bool _exiting;
    private bool _trayDisposed;

    private readonly Label _lblStatus;
    private readonly TabControl _tabs;
    private readonly TabPage _tabJobs;
    private readonly TabPage _tabLogs;
    private ListView _lvJobs = null!;
    private Button _btnToggle = null!;
    private readonly ComboBox _cboLogJob = new();
    private readonly TextBox _txtLogCount = new();
    private ListView _lvLogs = null!;
    private TextBox _txtLogDetail = null!;

    public MainForm(CronService service)
    {
        _service = service;

        Text = "wincron";
        Font = new Font("Microsoft YaHei UI", 9f);
        Size = new Size(840, 545);
        MinimumSize = new Size(680, 440);
        StartPosition = FormStartPosition.CenterScreen;

        _lblStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
            Text = "调度：运行中",
        };

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabJobs = new TabPage("任务");
        _tabLogs = new TabPage("日志");
        _tabs.TabPages.Add(_tabJobs);
        _tabs.TabPages.Add(_tabLogs);
        _tabs.SelectedIndexChanged += (_, _) => { if (_tabs.SelectedTab == _tabLogs) RefreshLogs(); };

        BuildJobsTab();
        BuildLogsTab();

        // 先加 Fill 再加 Top（WinForms 按加入逆序布局）
        Controls.Add(_tabs);
        Controls.Add(_lblStatus);

        // 托盘（SPEC §8）：图标从内嵌多尺寸 ico 加载（SPEC §11），失败退回系统图标
        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "wincron",
            Visible = true,
            ContextMenuStrip = _trayMenu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        RebuildTrayMenu();

        Load += OnLoaded;
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => HideTrayIcon();

        _service.JobStarted += OnServiceJobStarted;
        _service.JobFinished += OnServiceJobFinished;
        _service.JobsChanged += OnServiceJobsChanged;
        _service.OpenRequested += OnServiceOpen;
        _service.QuitRequested += OnServiceQuit;
    }

    // ---- 构建布局 ----

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>从内嵌资源 WinCron.app.ico 加载应用图标（多尺寸），失败退回 SystemIcons.Application。</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var stream = typeof(MainForm).Assembly.GetManifestResourceStream("WinCron.app.ico");
            if (stream != null)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }

            AppLog.Error("embedded resource WinCron.app.ico not found");
        }
        catch (Exception ex)
        {
            AppLog.Error($"load app icon failed: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(6, 9, 3, 0),
    };

    private static void EnableDoubleBuffer(ListView listView)
    {
        typeof(ListView).GetProperty("DoubleBuffered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(listView, true);
    }

    private void BuildJobsTab()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6, 7, 0, 0),
        };
        _btnToggle = MakeButton("禁用", ToggleSelected);
        buttons.Controls.AddRange(new Control[]
        {
            MakeButton("新建", () => EditJob(null)),
            MakeButton("编辑", EditSelected),
            MakeButton("删除", DeleteSelected),
            MakeButton("立即运行", RunSelected),
            _btnToggle,
            MakeButton("刷新", () => { _service.ReloadJobsFromDisk(); RefreshAll(); }),
        });

        _lvJobs = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _lvJobs.Columns.Add("名称", 150);
        _lvJobs.Columns.Add("文件夹", 100);
        _lvJobs.Columns.Add("Cron", 140);
        _lvJobs.Columns.Add("下次运行", 150);
        _lvJobs.Columns.Add("上次运行", 155);
        _lvJobs.Columns.Add("状态", 105);
        _lvJobs.Columns.Add("已执行", 65);
        _lvJobs.SelectedIndexChanged += (_, _) => UpdateToggleText();
        _lvJobs.DoubleClick += (_, _) => EditSelected();
        EnableDoubleBuffer(_lvJobs);

        _tabJobs.Controls.Add(_lvJobs);
        _tabJobs.Controls.Add(buttons);
    }

    private void BuildLogsTab()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6, 7, 0, 0),
        };
        _cboLogJob.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboLogJob.Width = 170;
        _txtLogCount.Width = 55;
        _txtLogCount.Text = "100";
        top.Controls.Add(MakeCaption("任务:"));
        top.Controls.Add(_cboLogJob);
        top.Controls.Add(MakeCaption("条数:"));
        top.Controls.Add(_txtLogCount);
        top.Controls.Add(MakeButton("查询", RefreshLogs));
        top.Controls.Add(MakeButton("清空该任务日志", ClearJobLogs));
        top.Controls.Add(MakeButton("清空全部日志", ClearAllLogs));

        _txtLogDetail = new TextBox
        {
            Dock = DockStyle.Bottom,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
            Height = 185,
            BackColor = SystemColors.Window,
        };

        _lvLogs = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _lvLogs.Columns.Add("开始时间", 140);
        _lvLogs.Columns.Add("任务", 120);
        _lvLogs.Columns.Add("触发", 60);
        _lvLogs.Columns.Add("退出码", 70);
        _lvLogs.Columns.Add("错误摘要", 320);
        _lvLogs.SelectedIndexChanged += (_, _) => ShowLogDetail();
        EnableDoubleBuffer(_lvLogs);

        _tabLogs.Controls.Add(_lvLogs);
        _tabLogs.Controls.Add(_txtLogDetail);
        _tabLogs.Controls.Add(top);
    }

    // ---- 服务事件（后台线程 → UI 线程） ----

    private void OnServiceJobStarted(JobLogEntry entry) => UiSafe(() =>
    {
        RefreshJobsView();
        RefreshLogsIfActive();
    });

    private void OnServiceJobFinished(JobLogEntry entry) => UiSafe(() =>
    {
        RefreshJobsView();
        RefreshLogsIfActive();
        ShowFailBalloon(entry);
    });

    private void OnServiceJobsChanged() => UiSafe(RefreshAll);

    private void OnServiceOpen() => UiSafe(ShowFromTray);

    private void OnServiceQuit() => UiSafe(ExitApp);

    private void UiSafe(Action action)
    {
        try
        {
            if (IsDisposed || Disposing || !IsHandleCreated)
            {
                return;
            }

            if (InvokeRequired)
            {
                _ = BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // ---- 窗口生命周期 ----

    private void OnLoaded(object? sender, EventArgs e)
    {
        RefreshAll();
        if (_service.Degraded)
        {
            ShowTrayBalloon("wincron", "数据目录不可用（权限？），调度已禁用", ToolTipIcon.Warning);
        }
        else if (!_service.GlobalEnabled)
        {
            ShowTrayBalloon("wincron", "调度处于暂停状态（可在托盘菜单恢复）", ToolTipIcon.Info);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 关闭 = 隐藏到托盘；退出仅经托盘菜单 / IPC quit / 系统关机
        if (!_exiting
            && e.CloseReason is not (CloseReason.ApplicationExitCall
                or CloseReason.WindowsShutDown
                or CloseReason.TaskManagerClosing))
        {
            e.Cancel = true;
            Hide();
            return;
        }

        HideTrayIcon();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        HideTrayIcon();
        AppLog.Info("wincron exiting (requested by ui/ipc)");
        Application.Exit();
    }

    private void HideTrayIcon()
    {
        if (_trayDisposed)
        {
            return;
        }

        _trayDisposed = true;
        try { _tray.Visible = false; } catch { }
        try { _tray.Dispose(); } catch { }
    }

    // ---- 刷新 ----

    private void RefreshAll()
    {
        _lblStatus.Text = "调度：" + (_service.GlobalEnabled ? "运行中" : "已暂停")
            + (_service.Degraded ? "（数据目录不可用，调度禁用）" : "");
        RefreshJobsView();
        RefreshLogJobCombo();
        RefreshLogsIfActive();
        RebuildTrayMenu();
    }

    private void RefreshJobsView()
    {
        var jobs = _service.GetJobs();
        _jobsById = jobs.ToDictionary(j => j.Id, j => j, StringComparer.Ordinal);

        _lvJobs.BeginUpdate();
        _lvJobs.Items.Clear();
        foreach (var job in jobs)
        {
            string status;
            if (!job.Enabled)
            {
                status = "禁用";
            }
            else if (job.ConsecutiveFailures > 0)
            {
                status = $"连续失败{job.ConsecutiveFailures}";
            }
            else
            {
                status = "启用";
            }

            string nextText;
            if (!job.Enabled)
            {
                nextText = "-";
            }
            else
            {
                var next = _service.GetNextRunText(job);
                nextText = next == "startup" ? "开机" : TryFormatLocal(next) ?? "-";
            }

            var lastText = TryFormatLocal(job.LastExecutedAt) ?? "-";
            var item = new ListViewItem(job.Name) { Tag = job.Id };
            item.SubItems.Add(job.Folder ?? "");
            item.SubItems.Add(job.Cron ?? "");
            item.SubItems.Add(nextText);
            item.SubItems.Add(lastText);
            item.SubItems.Add(status);
            item.SubItems.Add(job.ExecutedCount.ToString());
            _lvJobs.Items.Add(item);
        }

        _lvJobs.EndUpdate();
        UpdateToggleText();
    }

    private static string? TryFormatLocal(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return null;
        }

        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : null;
    }

    private Job? SelectedJob
    {
        get
        {
            if (_lvJobs.SelectedItems.Count == 0)
            {
                return null;
            }

            return _jobsById.TryGetValue((string)_lvJobs.SelectedItems[0].Tag!, out var job) ? job : null;
        }
    }

    private void UpdateToggleText()
    {
        var job = SelectedJob;
        _btnToggle.Text = job is { Enabled: true } ? "禁用" : "启用";
    }

    // ---- 任务操作 ----

    private void EditJob(Job? source)
    {
        using var dialog = new JobEditorForm(source);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var result = dialog.Result;
        try
        {
            if (source == null)
            {
                _service.AddJob(result);
            }
            else
            {
                _service.UpdateJob(result);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void EditSelected()
    {
        var job = SelectedJob;
        if (job == null)
        {
            MessageBox.Show(this, "请先选择任务", "wincron", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        EditJob(job);
    }

    private void DeleteSelected()
    {
        var job = SelectedJob;
        if (job == null)
        {
            MessageBox.Show(this, "请先选择任务", "wincron", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this, $"确定删除任务“{job.Name}”吗？", "确认删除",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            _service.DeleteJob(job.Id);
        }
    }

    private void RunSelected()
    {
        var job = SelectedJob;
        if (job == null)
        {
            MessageBox.Show(this, "请先选择任务", "wincron", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var (ok, error) = _service.StartJob(job, TriggerSources.Ui);
        if (!ok)
        {
            MessageBox.Show(this, error, "无法运行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ToggleSelected()
    {
        var job = SelectedJob;
        if (job == null)
        {
            MessageBox.Show(this, "请先选择任务", "wincron", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _service.SetJobEnabled(job.Id, !job.Enabled);
    }

    // ---- 日志 Tab ----

    private void RefreshLogJobCombo()
    {
        var selected = _cboLogJob.SelectedItem as string;
        _cboLogJob.Items.Clear();
        _cboLogJob.Items.Add("全部");
        foreach (var job in _service.GetJobs())
        {
            _cboLogJob.Items.Add(job.Name);
        }

        var index = selected != null ? _cboLogJob.Items.IndexOf(selected) : 0;
        _cboLogJob.SelectedIndex = index >= 0 ? index : 0;
    }

    private void RefreshLogsIfActive()
    {
        if (_tabs.SelectedTab == _tabLogs)
        {
            RefreshLogs();
        }
    }

    private void RefreshLogs()
    {
        if (_cboLogJob.Items.Count == 0 || _cboLogJob.SelectedIndex < 0)
        {
            return;
        }

        var name = _cboLogJob.SelectedIndex == 0 ? null : (string)_cboLogJob.SelectedItem!;
        var n = int.TryParse(_txtLogCount.Text.Trim(), out var parsed) ? parsed : 100;
        if (n <= 0)
        {
            n = 100;
        }

        if (n > 1000)
        {
            n = 1000;
        }

        var logs = _service.GetLogs(name, n);
        _lvLogs.BeginUpdate();
        _lvLogs.Items.Clear();
        foreach (var entry in logs)
        {
            var started = DateTimeOffset.FromUnixTimeMilliseconds(entry.StartedAt)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            var item = new ListViewItem(started) { Tag = entry };
            item.SubItems.Add(entry.JobName);
            item.SubItems.Add(entry.TriggerSource);
            item.SubItems.Add(entry.Running ? "运行中" : entry.ExitCode.ToString());
            var summary = entry.Running ? "" : FirstLine(entry.Error, 80);
            item.SubItems.Add(summary);
            _lvLogs.Items.Add(item);
        }

        _lvLogs.EndUpdate();
    }

    private void ShowLogDetail()
    {
        if (_lvLogs.SelectedItems.Count == 0)
        {
            _txtLogDetail.Clear();
            return;
        }

        var entry = (JobLogEntry)_lvLogs.SelectedItems[0].Tag!;
        var sb = new StringBuilder();
        sb.AppendLine($"任务: {entry.JobName}    触发: {entry.TriggerSource}    命令: {entry.CommandLine}");
        if (entry.Running)
        {
            sb.AppendLine("状态: 运行中");
        }
        else
        {
            var finished = DateTimeOffset.FromUnixTimeMilliseconds(entry.FinishedAt)
                .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            sb.AppendLine($"退出码: {entry.ExitCode}    结束: {finished}");
        }

        sb.AppendLine();
        sb.AppendLine("------ stdout ------");
        sb.AppendLine(entry.Stdout.Length == 0 ? "(空)" : entry.Stdout);
        sb.AppendLine("------ stderr ------");
        sb.AppendLine(entry.Stderr.Length == 0 ? "(空)" : entry.Stderr);
        sb.AppendLine("------ error ------");
        sb.AppendLine(entry.Error.Length == 0 ? "(无)" : entry.Error);
        _txtLogDetail.Text = sb.ToString();
    }

    private void ClearJobLogs()
    {
        if (_cboLogJob.SelectedIndex <= 0)
        {
            MessageBox.Show(this, "请先在下拉框中选择具体任务", "wincron", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var name = (string)_cboLogJob.SelectedItem!;
        var answer = MessageBox.Show(this, $"确定清空任务“{name}”的全部日志吗？", "确认清空",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            _service.ClearLogs(name);
            RefreshLogs();
        }
    }

    private void ClearAllLogs()
    {
        var answer = MessageBox.Show(this, "确定清空全部任务日志吗？", "确认清空",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            _service.ClearLogs(null);
            RefreshLogs();
        }
    }

    // ---- 托盘 ----

    private void RebuildTrayMenu()
    {
        if (_trayDisposed)
        {
            return;
        }

        _trayMenu.Items.Clear();
        _trayMenu.Items.Add("打开主窗口", null, (_, _) => ShowFromTray());
        _trayMenu.Items.Add(new ToolStripSeparator());
        foreach (var job in _service.GetJobs().Where(j => j.Enabled))
        {
            var name = job.Name;
            _trayMenu.Items.Add($"立即运行 {name}", null, (_, _) =>
            {
                var target = _service.FindJob(name) ?? job;
                var (ok, error) = _service.StartJob(target, TriggerSources.Ui);
                if (!ok)
                {
                    UiSafe(() => MessageBox.Show(this, error, "无法运行", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                }
            });
        }

        _trayMenu.Items.Add(new ToolStripSeparator());
        var pauseItem = new ToolStripMenuItem(_service.GlobalEnabled ? "暂停调度" : "恢复调度");
        pauseItem.Click += (_, _) =>
        {
            _service.SetGlobalEnabled(!_service.GlobalEnabled);
        };
        _trayMenu.Items.Add(pauseItem);
        _trayMenu.Items.Add("退出", null, (_, _) => ExitApp());
    }

    private void ShowTrayBalloon(string title, string text, ToolTipIcon icon)
    {
        if (_trayDisposed)
        {
            return;
        }

        try { _tray.ShowBalloonTip(4000, title, text, icon); } catch { }
    }

    /// <summary>任务失败气球提示，同任务 10 分钟内不重复（SPEC §8）。</summary>
    private void ShowFailBalloon(JobLogEntry entry)
    {
        if (entry.Running || (entry.ExitCode == 0 && string.IsNullOrEmpty(entry.Error)))
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (_lastFailBalloon.TryGetValue(entry.JobId, out var last) && nowUtc - last < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastFailBalloon[entry.JobId] = nowUtc;
        var message = $"{entry.JobName} 退出码 {entry.ExitCode}";
        if (!string.IsNullOrEmpty(entry.Error))
        {
            message += "：" + FirstLine(entry.Error, 120);
        }

        ShowTrayBalloon("任务失败", message, ToolTipIcon.Error);
    }

    private static string FirstLine(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var line = text.Split('\n')[0].TrimEnd('\r');
        return line.Length <= max ? line : line[..max] + "…";
    }
}
