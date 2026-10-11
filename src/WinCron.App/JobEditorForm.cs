using Cronos;
using WinCron.Core;

namespace WinCron.App;

/// <summary>
/// 任务编辑对话框（SPEC §8）：全部手写布局；OK/取消；名称/命令必填，Cron 可解析（@reboot 放行）。
/// </summary>
internal sealed class JobEditorForm : Form
{
    private readonly Job _job;
    private readonly bool _isNew;

    private readonly TextBox _txtName = new();
    private readonly TextBox _txtFolder = new();
    private readonly TextBox _txtCron = new();
    private readonly TextBox _txtCommand = new();
    private readonly TextBox _txtArgs = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _txtWorkDir = new();
    private readonly NumericUpDown _numTimeout = new() { Minimum = 0, Maximum = 86400 };
    private readonly NumericUpDown _numMaxFail = new() { Minimum = 0, Maximum = 100000 };
    private readonly ComboBox _cboPolicy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _chkNoWindow = new() { Text = "CREATE_NO_WINDOW（隐藏子进程窗口）", AutoSize = true };
    private readonly CheckBox _chkEnabled = new() { Text = "启用", AutoSize = true };
    private readonly Label _lblPreview = new()
    {
        AutoSize = false,
        Height = 20,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
    };
    private readonly Button _btnPreview = new() { Text = "预览下次运行", Width = 112 };
    private readonly Button _btnOk = new() { Text = "确定", DialogResult = DialogResult.OK, MinimumSize = new Size(84, 28) };
    private readonly Button _btnCancel = new() { Text = "取消", DialogResult = DialogResult.Cancel, MinimumSize = new Size(84, 28) };

    public Job Result => _job;

    public JobEditorForm(Job? source)
    {
        _isNew = source == null;
        _job = source?.Clone() ?? new Job { Enabled = true, ConcurrencyPolicy = "skip" };

        Text = _isNew ? "新建任务" : $"编辑任务 - {_job.Name}";
        Font = new Font("Microsoft YaHei UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 452);

        _cboPolicy.Items.AddRange(new object[] { "allow", "skip", "kill_old" });
        _cboPolicy.SelectedItem = NormalizePolicy(_job.ConcurrencyPolicy);

        _txtName.Text = _job.Name;
        _txtFolder.Text = _job.Folder;
        _txtCron.Text = _job.Cron;
        _txtCommand.Text = _job.Command;
        _txtArgs.Text = string.Join(Environment.NewLine, _job.Args ?? new List<string>());
        _txtWorkDir.Text = _job.WorkDir;
        _numTimeout.Value = Clamp(_job.Timeout, 0, 86400);
        _numMaxFail.Value = Clamp(_job.MaxConsecutiveFailures, 0, 100000);
        _chkNoWindow.Checked = string.Equals(_job.FlagProcessCreation, "CREATE_NO_WINDOW", StringComparison.OrdinalIgnoreCase);
        _chkEnabled.Checked = _job.Enabled;

        var tips = new ToolTip();
        tips.SetToolTip(_txtCron, "标准 5 段式 cron（分 时 日 月 周），或 @reboot 表示开机运行");
        tips.SetToolTip(_txtArgs, "每行一个参数");
        tips.SetToolTip(_numTimeout, "超时秒数，0 表示不限时");
        tips.SetToolTip(_numMaxFail, "连续失败达到该次数自动禁用任务，0 表示不限");

        _btnPreview.Click += (_, _) => PreviewCron();
        _btnOk.Click += (_, _) => { if (!ValidateAndApply()) DialogResult = DialogResult.None; };

        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        // Cron 行：输入框 + 预览按钮
        var cronPanel = new Panel { Dock = DockStyle.Fill, Height = 26 };
        _btnPreview.Dock = DockStyle.Right;
        _txtCron.Dock = DockStyle.Fill;
        cronPanel.Controls.Add(_txtCron);
        cronPanel.Controls.Add(_btnPreview);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10, 8, 10, 4),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, "名称", _txtName, 30);
        AddRow(table, "文件夹", _txtFolder, 30);
        AddRow(table, "Cron", cronPanel, 30);
        AddPreviewRow(table);
        AddRow(table, "命令", _txtCommand, 30);
        AddRow(table, "参数", _txtArgs, 76);
        AddRow(table, "工作目录", _txtWorkDir, 30);
        AddRow(table, "超时秒", _numTimeout, 30);
        AddRow(table, "并发策略", _cboPolicy, 30);
        AddRow(table, "最大连续失败", _numMaxFail, 30);
        AddRow(table, "", _chkNoWindow, 28);
        AddRow(table, "", _chkEnabled, 28);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        bottom.Controls.Add(_btnCancel);
        bottom.Controls.Add(_btnOk);

        Controls.Add(table);
        Controls.Add(bottom);
    }

    private static string NormalizePolicy(string? policy)
    {
        var v = (policy ?? "").Trim().ToLowerInvariant();
        return v is "allow" or "skip" or "kill_old" ? v : "skip";
    }

    private static decimal Clamp(int value, int min, int max) => Math.Clamp((decimal)value, min, max);

    private static void AddRow(TableLayoutPanel table, string label, Control control, int height)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        table.Controls.Add(new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
        }, 0, row);
        control.Dock = control is CheckBox or NumericUpDown or ComboBox ? DockStyle.Left : DockStyle.Fill;
        table.Controls.Add(control, 1, row);
    }

    private void AddPreviewRow(TableLayoutPanel table)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        table.Controls.Add(new Label(), 0, row);
        table.Controls.Add(_lblPreview, 1, row);
    }

    private void PreviewCron()
    {
        var cron = _txtCron.Text.Trim();
        if (CronUtil.IsReboot(cron))
        {
            SetPreview("开机任务：系统启动时运行一次", Color.Green);
            return;
        }

        try
        {
            var expr = CronExpression.Parse(cron, CronFormat.Standard);
            // Cronos 要求 from 为 Kind=Utc，返回值也是 Kind=Utc → 展示前转本地时间
            var nextUtc = CronUtil.NextUtc(expr);
            SetPreview(nextUtc.HasValue
                ? "下次运行：" + nextUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "该 Cron 没有下一次运行时间", Color.Green);
        }
        catch (Exception ex)
        {
            SetPreview("解析失败：" + ex.Message, Color.Red);
        }
    }

    private void SetPreview(string text, Color color)
    {
        _lblPreview.Text = text;
        _lblPreview.ForeColor = color;
    }

    private bool ValidateAndApply()
    {
        var name = _txtName.Text.Trim();
        var command = _txtCommand.Text.Trim();
        var cron = _txtCron.Text.Trim();

        if (name.Length == 0)
        {
            return Warn("名称不能为空");
        }

        if (command.Length == 0)
        {
            return Warn("命令不能为空");
        }

        if (!CronUtil.IsReboot(cron))
        {
            try
            {
                CronExpression.Parse(cron, CronFormat.Standard);
            }
            catch (Exception ex)
            {
                return Warn("Cron 无法解析：" + ex.Message);
            }
        }

        _job.Name = name;
        _job.Folder = _txtFolder.Text.Trim();
        _job.Cron = cron;
        _job.Command = command;
        _job.Args = _txtArgs.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _job.WorkDir = _txtWorkDir.Text.Trim();
        _job.Timeout = (int)_numTimeout.Value;
        _job.ConcurrencyPolicy = _cboPolicy.SelectedItem as string ?? "skip";
        _job.MaxConsecutiveFailures = (int)_numMaxFail.Value;
        _job.FlagProcessCreation = _chkNoWindow.Checked ? "CREATE_NO_WINDOW" : "";
        _job.Enabled = _chkEnabled.Checked;
        _job.RunAtStartup = CronUtil.IsReboot(cron);
        return true;
    }

    private bool Warn(string message)
    {
        MessageBox.Show(this, message, "wincron", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
}
