using System.Text.Json;

namespace AgentStatus;

public sealed class DetailsForm : Form
{
    readonly Label heading = new() { Dock = DockStyle.Top, Height = 56, Padding = new Padding(14), Text = "正在连接…", Font = new Font("Microsoft YaHei UI", 13) };
    readonly DataGridView table = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BackgroundColor = SystemColors.Window };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly Label footer = new() { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(10), Text = "只显示状态，不代替回答或授权。清除提醒后状态将标记为未知。" };
    bool updating;
    public DetailsForm()
    {
        Text = "Agent 状态 · TrafficMonitor"; Size = new Size(920, 470); MinimumSize = new Size(650, 350);
        Font = new Font("Microsoft YaHei UI", 9); StartPosition = FormStartPosition.CenterScreen;
        foreach (var (field, label, width) in new[] { ("Source", "来源", 110), ("Project", "项目", 170), ("State", "状态", 100), ("Tools", "工具数", 65), ("UpdatedAt", "最近事件", 180), ("Note", "说明", 220) })
            table.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = field, HeaderText = label, Width = width });
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
        Controls.Add(table); Controls.Add(heading); Controls.Add(actions); Controls.Add(footer);
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
            heading.Text = "AI：" + snapshot.Text;
            table.DataSource = snapshot.Sessions;
            if (key != null) foreach (DataGridViewRow row in table.Rows) if ((row.DataBoundItem as SessionView)?.Key == key) { table.CurrentCell = row.Cells[0]; break; }
        }
        catch { if (!IsDisposed) heading.Text = "接收器未连接 · 可点击下方按钮启动"; }
        finally { updating = false; }
    }
}
