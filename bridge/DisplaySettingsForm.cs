namespace AgentStatus;

public sealed class DisplaySettingsForm : Form
{
    readonly SettingsStore settings;
    readonly Dictionary<string, CheckBox> sourceBoxes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TextBox> textBoxes = new(StringComparer.OrdinalIgnoreCase);
    readonly Label status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 9, 4, 4) };

    public DisplaySettingsForm(SettingsStore settings)
    {
        this.settings = settings;
        Text = "显示来源与状态文本";
        Size = new Size(720, 680);
        MinimumSize = new Size(560, 480);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9);

        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var sources = new GroupBox { Text = "显示哪些工具", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var sourceTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = (AgentStatusSettings.SourceDefinitions.Count + 1) / 2 };
        sourceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sourceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < AgentStatusSettings.SourceDefinitions.Count; i++)
        {
            if (i % 2 == 0) sourceTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var definition = AgentStatusSettings.SourceDefinitions[i];
            var check = new CheckBox { Text = definition.Label, AutoSize = true, Margin = new Padding(4, 5, 12, 5), Tag = definition.Id };
            sourceBoxes[definition.Id] = check;
            sourceTable.Controls.Add(check, i % 2, i / 2);
        }
        sources.Controls.Add(sourceTable);
        layout.Controls.Add(sources, 0, 0);

        var texts = new GroupBox { Text = "状态显示文本（只改变文字，不改变内部状态）", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var textTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = AgentStatusSettings.DisplayDefinitions.Count, Padding = new Padding(2) };
        textTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        textTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < AgentStatusSettings.DisplayDefinitions.Count; row++)
        {
            var definition = AgentStatusSettings.DisplayDefinitions[row];
            textTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            textTable.Controls.Add(new Label { Text = definition.Label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 8, 8, 4) }, 0, row);
            var input = new TextBox { Dock = DockStyle.Fill, MinimumSize = new Size(180, 28), Margin = new Padding(2, 3, 8, 3), MaxLength = 32 };
            textBoxes[definition.Key] = input;
            textTable.Controls.Add(input, 1, row);
        }
        texts.Controls.Add(textTable);
        layout.Controls.Add(texts, 0, 1);

        var note = new Label { Text = "未勾选的工具仍会被后台接收，但不会参与任务栏和详情列表显示。", AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(6, 10, 6, 8) };
        layout.Controls.Add(note, 0, 2);
        content.Controls.Add(layout);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8), WrapContents = false };
        var save = new Button { Text = "保存设置", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        var reset = new Button { Text = "恢复默认", AutoSize = true };
        reset.Click += (_, _) => LoadValues(AgentStatusSettings.Defaults, "已恢复默认值，点击保存后生效");
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(save); bottom.Controls.Add(reset); bottom.Controls.Add(cancel); bottom.Controls.Add(status);
        Controls.Add(content); Controls.Add(bottom);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => LoadValues(settings.Current, "");
    }

    void LoadValues(AgentStatusSettings config, string message)
    {
        foreach (var definition in AgentStatusSettings.SourceDefinitions)
            sourceBoxes[definition.Id].Checked = config.EnabledSources.GetValueOrDefault(definition.Id, true);
        foreach (var definition in AgentStatusSettings.DisplayDefinitions)
            textBoxes[definition.Key].Text = config.DisplayTexts.GetValueOrDefault(definition.Key, definition.DefaultText);
        status.Text = message;
    }

    void SaveSettings()
    {
        try
        {
            var current = settings.Current;
            var enabled = new Dictionary<string, bool>(current.EnabledSources, StringComparer.OrdinalIgnoreCase);
            foreach (var definition in AgentStatusSettings.SourceDefinitions) enabled[definition.Id] = sourceBoxes[definition.Id].Checked;
            var texts = new Dictionary<string, string>(current.DisplayTexts, StringComparer.OrdinalIgnoreCase);
            foreach (var definition in AgentStatusSettings.DisplayDefinitions) texts[definition.Key] = textBoxes[definition.Key].Text;
            settings.Save(current with { EnabledSources = enabled, DisplayTexts = texts });
            status.Text = "已保存，任务栏会自动更新";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            status.Text = "保存失败，请检查配置目录权限";
        }
    }
}
