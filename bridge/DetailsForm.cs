using System.Text.Json;
namespace AgentStatus;

public sealed class DetailsForm : Form
{
    readonly SettingsStore settings = new();
    readonly Label heading = new() { Dock = DockStyle.Top, Height = 68, Padding = new Padding(12), Text = "正在连接显示桥接…", Font = new Font("Microsoft YaHei UI", 12), AutoEllipsis = true };
    readonly CheckBox showSource = new() { Text = "显示来源", AutoSize = true };
    readonly NumericUpDown completion = new() { Minimum = 0, Maximum = 60, Width = 65 };
    readonly NumericUpDown notice = new() { Minimum = 1, Maximum = 600, Width = 65 };
    readonly NumericUpDown working = new() { Minimum = 1, Maximum = 120, Width = 65 };
    readonly NumericUpDown waiting = new() { Minimum = 1, Maximum = 1440, Width = 65 };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(650, 0), Margin = new Padding(4, 6, 4, 6) };
    readonly Button connect = new() { Text = "发现服务并连接", AutoSize = true };
    readonly CancellationTokenSource lifetime = new();
    readonly DataGridView table = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = SystemColors.Window };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    string[] knownSources = [];
    bool updating;
    public DetailsForm()
    {
        Text = "Agent 状态 · Runtime"; Size = new Size(1000, 680); MinimumSize = new Size(740, 600);
        Font = new Font("Microsoft YaHei UI", 9); StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        var config = settings.Current;
        showSource.Checked = config.ShowSource;
        completion.Value = config.CompletionToastMs / 1000; notice.Value = config.NoticeToastMs / 1000;
        working.Value = Math.Max(1, config.MaxWorkingMs / 60000); waiting.Value = config.MaxWaitingMs / 60000;
        var options = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 0, 10, 8) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var connectionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        connect.Click += async (_, _) => await DiscoverAndConnect();
        connectionRow.Controls.Add(connect); connectionRow.Controls.Add(status);
        options.Controls.Add(connectionRow, 0, 0);
        var timings = new GroupBox { Text = "显示时长与无进展超时（采集和主动上报共用）", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        var timeGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 2 };
        timeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        timeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var timeFields = new[] { ("完成提示（秒）", completion), ("提醒显示（秒）", notice), ("工作超时（分）", working), ("确认超时（分）", waiting) };
        for (int i = 0; i < timeFields.Length; i++)
        {
            var (label, input) = timeFields[i];
            input.Width = 85; input.Margin = new Padding(6, 5, 18, 5);
            timeGrid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 7, 4, 7) }, i % 2 * 2, i / 2);
            timeGrid.Controls.Add(input, i % 2 * 2 + 1, i / 2);
        }
        timings.Controls.Add(timeGrid); options.Controls.Add(timings, 0, 1);
        var displayRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        showSource.Margin = new Padding(4, 9, 16, 4); displayRow.Controls.Add(showSource);
        var save = new Button { Text = "保存设置", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                settings.Save(settings.Current with { ShowSource = showSource.Checked,
                    CompletionToastMs = (int)completion.Value * 1000, NoticeToastMs = (int)notice.Value * 1000,
                    MaxWorkingMs = (int)working.Value * 60000, MaxWaitingMs = (int)waiting.Value * 60000 });
                status.Text = "已保存";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { status.Text = "保存失败，请检查配置目录权限"; }
        };
        var display = new Button { Text = "来源与文本", AutoSize = true };
        display.Click += (_, _) => { using var dialog = new DisplaySettingsForm(settings, knownSources); dialog.ShowDialog(this); };
        displayRow.Controls.Add(save); displayRow.Controls.Add(display); options.Controls.Add(displayRow, 0, 2);
        foreach (var (field, label, width) in new[] { ("Source", "来源", 100), ("Origin", "方式", 100), ("State", "状态", 110), ("Tools", "工具数", 65), ("Confirmations", "确认数", 65), ("SessionId", "会话", 240), ("OperationId", "轮次", 190), ("UpdatedAt", "最近活动", 180) })
            table.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = field, HeaderText = label, Width = width });
        table.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex >= 0 && table.Columns[e.ColumnIndex].DataPropertyName == "Origin" && e.Value is string origin)
            { e.Value = origin == "external" ? "主动上报" : "采集"; e.FormattingApplied = true; }
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
        var clear = new Button { Text = "清除所选提醒", AutoSize = true };
        clear.Click += async (_, _) =>
        {
            if (table.CurrentRow?.DataBoundItem is SessionView selected)
                try { await Wire.Request(new { op = "ack", key = selected.Key }); await RefreshState(); } catch { heading.Text = "显示桥接未连接"; }
        };
        actions.Controls.Add(clear);
        Controls.Add(table); Controls.Add(options); Controls.Add(heading); Controls.Add(actions);
        timer.Tick += async (_, _) => await RefreshState();
        Shown += async (_, _) => { timer.Start(); await RefreshState(); };
        FormClosed += (_, _) => { timer.Dispose(); lifetime.Cancel(); };
    }
    async Task DiscoverAndConnect()
    {
        connect.Enabled = false; status.Text = "正在发现并连接 Runtime…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await Program.EnsureStarted();
            var result = await Wire.Request(new { op = "connect" }, 2000);
            if (!result.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException("后台桥接版本不兼容，请更新插件并退出旧版桥接");
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var snapshot = (await Wire.Request(new { op = "snapshot" }, 1500)).Deserialize<Snapshot>(JsonWire.Options)!;
                if (IsDisposed) return;
                status.Text = snapshot.RuntimeStatus;
                if (snapshot.Connected) { status.Text = "Runtime 已连接"; await RefreshState(); return; }
                if (snapshot.RuntimeStatus.StartsWith("未发现") || snapshot.RuntimeStatus.StartsWith("Runtime 服务")) return;
                await Task.Delay(200, timeout.Token);
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "连接超时，请检查 Runtime 服务运行情况"; }
        catch (UnauthorizedAccessException) { if (!IsDisposed) status.Text = "后台桥接访问被拒绝，请退出旧版桥接后重试"; }
        catch (InvalidOperationException error) { if (!IsDisposed) status.Text = error.Message; }
        catch (Exception error) when (error is IOException or TimeoutException or System.ComponentModel.Win32Exception)
        { if (!IsDisposed) status.Text = "后台桥接启动或连接失败，请检查程序和权限"; }
        finally { if (!IsDisposed) connect.Enabled = true; }
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
