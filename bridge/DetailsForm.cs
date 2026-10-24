using System.Text.Json;
namespace AgentStatus;

public sealed class DetailsForm : Form
{
    readonly SettingsStore settings = new();
    readonly Label heading = new() { Dock = DockStyle.Top, Height = 68, Padding = new Padding(12), Text = "正在连接显示桥接…", Font = new Font("Microsoft YaHei UI", 12), AutoEllipsis = true };
    readonly TextBox runtimeDirectory = new() { Width = 440 };
    readonly CheckBox showSource = new() { Text = "显示来源", AutoSize = true };
    readonly NumericUpDown completion = new() { Minimum = 0, Maximum = 60, Width = 65 };
    readonly NumericUpDown notice = new() { Minimum = 1, Maximum = 600, Width = 65 };
    readonly NumericUpDown working = new() { Minimum = 1, Maximum = 120, Width = 65 };
    readonly NumericUpDown waiting = new() { Minimum = 1, Maximum = 1440, Width = 65 };
    readonly Label status = new() { AutoSize = true };
    readonly DataGridView table = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = SystemColors.Window };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    string[] knownSources = [];
    bool updating;
    public DetailsForm()
    {
        Text = "Agent 状态 · Runtime"; Size = new Size(1000, 540); MinimumSize = new Size(740, 450);
        Font = new Font("Microsoft YaHei UI", 9); StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        var config = settings.Current;
        runtimeDirectory.Text = config.RuntimeDirectory; showSource.Checked = config.ShowSource;
        completion.Value = config.CompletionToastMs / 1000; notice.Value = config.NoticeToastMs / 1000;
        working.Value = Math.Max(1, config.MaxWorkingMs / 60000); waiting.Value = config.MaxWaitingMs / 60000;
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), WrapContents = true };
        options.Controls.Add(new Label { Text = "Runtime 目录", AutoSize = true }); options.Controls.Add(runtimeDirectory);
        var browse = new Button { Text = "选择目录", AutoSize = true };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { InitialDirectory = runtimeDirectory.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) runtimeDirectory.Text = dialog.SelectedPath; };
        options.Controls.Add(browse); options.SetFlowBreak(browse, true);
        options.Controls.Add(showSource);
        foreach (var pair in new[] { ("完成提示（秒）", completion), ("提醒显示（秒）", notice), ("工作超时（分）", working), ("确认超时（分）", waiting) })
        { options.Controls.Add(new Label { Text = pair.Item1, AutoSize = true }); options.Controls.Add(pair.Item2); }
        var save = new Button { Text = "保存设置", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                settings.Save(settings.Current with { RuntimeDirectory = runtimeDirectory.Text, ShowSource = showSource.Checked,
                    CompletionToastMs = (int)completion.Value * 1000, NoticeToastMs = (int)notice.Value * 1000,
                    MaxWorkingMs = (int)working.Value * 60000, MaxWaitingMs = (int)waiting.Value * 60000 });
                runtimeDirectory.Text = settings.Current.RuntimeDirectory; status.Text = "已保存";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { status.Text = "保存失败，请检查目录"; }
        };
        var display = new Button { Text = "来源与文本", AutoSize = true };
        display.Click += (_, _) => { using var dialog = new DisplaySettingsForm(settings, knownSources); dialog.ShowDialog(this); };
        options.Controls.Add(save); options.Controls.Add(display); options.Controls.Add(status);
        foreach (var (field, label, width) in new[] { ("Source", "来源", 100), ("Origin", "方式", 100), ("State", "状态", 110), ("Tools", "工具数", 65), ("Confirmations", "确认数", 65), ("SessionId", "会话", 240), ("OperationId", "轮次", 190), ("UpdatedAt", "最近活动", 180) })
            table.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = field, HeaderText = label, Width = width });
        table.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && table.Columns[e.ColumnIndex].DataPropertyName == "Origin" && e.Value is string origin)
            { e.Value = origin == "external" ? "主动上报" : "采集"; e.FormattingApplied = true; }
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
        var start = new Button { Text = "启动显示桥接", AutoSize = true };
        start.Click += async (_, _) => { try { await Program.EnsureStarted(); await RefreshState(); } catch { heading.Text = "显示桥接启动失败"; } };
        var clear = new Button { Text = "清除所选提醒", AutoSize = true };
        clear.Click += async (_, _) =>
        {
            if (table.CurrentRow?.DataBoundItem is SessionView selected)
                try { await Wire.Request(new { op = "ack", key = selected.Key }); await RefreshState(); } catch { heading.Text = "显示桥接未连接"; }
        };
        actions.Controls.Add(start); actions.Controls.Add(clear);
        Controls.Add(table); Controls.Add(options); Controls.Add(heading); Controls.Add(actions);
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
            var snapshot = (await Wire.Request(new { op = "snapshot" })).Deserialize<Snapshot>(JsonWire.Options)!;
            if (IsDisposed) return;
            knownSources = snapshot.KnownSources;
            heading.Text = snapshot.RuntimeStatus + (snapshot.Text.Length > 0 ? " · " + snapshot.Text : "");
            var selectedKey = (table.CurrentRow?.DataBoundItem as SessionView)?.Key;
            table.DataSource = snapshot.Sessions;
            foreach (DataGridViewRow row in table.Rows)
                if ((row.DataBoundItem as SessionView)?.Key == selectedKey) { table.CurrentCell = row.Cells[0]; break; }
        }
        catch { if (!IsDisposed) heading.Text = "显示桥接未连接"; }
        finally { updating = false; }
    }
}
