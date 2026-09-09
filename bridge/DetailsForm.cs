using System.Text.Json;

namespace AgentStatus;

public sealed class DetailsForm : Form
{
    readonly SettingsStore settings = new();
    readonly Label heading = new() { Dock = DockStyle.Top, Height = 78, Padding = new Padding(14), AutoEllipsis = true, Text = "正在连接…", Font = new Font("Microsoft YaHei UI", 13) };
    readonly FlowLayoutPanel settingsPanel = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 70), Padding = new Padding(8, 5, 8, 5), WrapContents = true, AutoScroll = false };
    readonly CheckBox showSource = new() { Text = "显示来源", AutoSize = true, Margin = new Padding(4, 7, 10, 3) };
    readonly NumericUpDown completionSeconds = new() { Minimum = 0, Maximum = 60, DecimalPlaces = 0, Width = 80, Margin = new Padding(2, 3, 10, 3) };
    readonly NumericUpDown maxWorkingMinutes = new() { Minimum = 1, Maximum = 120, DecimalPlaces = 0, Width = 80, Margin = new Padding(2, 3, 10, 3) };
    readonly NumericUpDown pollIntervalMs = new() { Minimum = 200, Maximum = 3000, Increment = 50, Width = 100, Margin = new Padding(2, 3, 10, 3) };
    readonly Label settingsStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 8, 4, 3) };
    readonly DataGridView table = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BackgroundColor = SystemColors.Window };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Label footer = new() { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(10), Text = "只显示状态，不代替回答或授权。清除提醒后状态将标记为未知。" };
    bool updating;
    public DetailsForm()
    {
        Text = "Agent 状态 · TrafficMonitor"; Size = new Size(920, 470); MinimumSize = new Size(650, 350);
        Font = new Font("Microsoft YaHei UI", 9); StartPosition = FormStartPosition.CenterScreen;
        LoadSettingsIntoControls();
        foreach (var (field, label, width) in new[] { ("Source", "来源", 110), ("Project", "项目", 170), ("State", "状态", 100), ("Tools", "工具数", 65), ("UpdatedAt", "最近事件", 180), ("Note", "说明", 220) })
            table.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = field, HeaderText = label, Width = width });
        settingsPanel.Controls.Add(showSource);
        settingsPanel.Controls.Add(new Label { Text = "完成提示（秒）", AutoSize = true, Margin = new Padding(4, 8, 2, 3) });
        settingsPanel.Controls.Add(completionSeconds);
        settingsPanel.Controls.Add(new Label { Text = "工作超时（分钟）", AutoSize = true, Margin = new Padding(4, 8, 2, 3) });
        settingsPanel.Controls.Add(maxWorkingMinutes);
        settingsPanel.Controls.Add(new Label { Text = "扫描间隔（毫秒）", AutoSize = true, Margin = new Padding(4, 8, 2, 3) });
        settingsPanel.Controls.Add(pollIntervalMs);
        var saveSettings = new Button { Text = "保存设置", AutoSize = true, Margin = new Padding(4, 3, 4, 3) };
        saveSettings.Click += (_, _) => SaveSettings();
        settingsPanel.Controls.Add(saveSettings);
        var resetSettings = new Button { Text = "恢复默认", AutoSize = true, Margin = new Padding(4, 3, 4, 3) };
        resetSettings.Click += (_, _) => { settings.ResetToDefaults(); LoadSettingsIntoControls(); settingsStatus.Text = "已恢复默认设置"; };
        settingsPanel.Controls.Add(resetSettings);
        settingsPanel.Controls.Add(settingsStatus);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 43, Padding = new Padding(8) };
        var start = new Button { Text = "启动接收器", AutoSize = true };
        start.Click += async (_, _) => { try { await Program.EnsureStarted(); await RefreshState(); } catch { heading.Text = "接收器启动失败，请查看诊断日志"; } };
        var ack = new Button { Text = "清除所选提醒", AutoSize = true };
        ack.Click += async (_, _) =>
        {
            if (table.CurrentRow?.DataBoundItem is SessionView selected)
            {
                try { await Wire.Request(new { op = "ack", key = selected.Key }); await RefreshState(); }
                catch { heading.Text = "接收器未连接"; }
            }
        };
        actions.Controls.Add(start); actions.Controls.Add(ack);
        Controls.Add(table); Controls.Add(settingsPanel); Controls.Add(heading); Controls.Add(actions); Controls.Add(footer);
        timer.Tick += async (_, _) => await RefreshState();
        Shown += async (_, _) => { timer.Start(); await RefreshState(); };
        FormClosed += (_, _) => timer.Dispose();
    }
    async Task RefreshState()
    {
        if (updating || IsDisposed) return;
        updating = true;
        try
        {
            var raw = await Wire.Request(new { op = "snapshot" });
            var snapshot = raw.Deserialize<Snapshot>(JsonWire.Options)!;
            if (IsDisposed) return;
            var key = (table.CurrentRow?.DataBoundItem as SessionView)?.Key;
            heading.Text = snapshot.Text.Length == 0 ? "无活动 Agent" : snapshot.Text;
            table.DataSource = snapshot.Sessions;
            if (key != null) foreach (DataGridViewRow row in table.Rows) if ((row.DataBoundItem as SessionView)?.Key == key) { table.CurrentCell = row.Cells[0]; break; }
        }
        catch { if (!IsDisposed) heading.Text = "接收器未连接 · 可点击下方按钮启动"; }
        finally { updating = false; }
    }

    void LoadSettingsIntoControls()
    {
        var config = settings.Current;
        showSource.Checked = config.ShowSource;
        completionSeconds.Value = config.CompletionToastMs / 1000;
        maxWorkingMinutes.Value = Math.Max(1, config.MaxWorkingMs / 60000);
        pollIntervalMs.Value = config.CodexPollIntervalMs;
    }

    void SaveSettings()
    {
        try
        {
            settings.Save(new AgentStatusSettings
            {
                ShowSource = showSource.Checked,
                CompletionToastMs = (int)completionSeconds.Value * 1000,
                MaxWorkingMs = (int)maxWorkingMinutes.Value * 60000,
                CodexPollIntervalMs = (int)pollIntervalMs.Value
            });
            settingsStatus.Text = "已保存";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            settingsStatus.Text = "保存失败，请检查配置目录权限";
        }
    }
}
