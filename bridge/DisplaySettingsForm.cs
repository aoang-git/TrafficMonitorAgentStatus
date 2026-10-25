namespace AgentStatus;

public sealed class DisplaySettingsForm : Form
{
    readonly SettingsStore settings;
    readonly Dictionary<string, CheckBox> sourceBoxes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TextBox> textBoxes = new(StringComparer.OrdinalIgnoreCase);
    readonly CheckBox showReported = new() { Text = "主动上报显示", AutoSize = true, Margin = new Padding(4, 6, 12, 4) };
    readonly Label status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 9, 4, 4) };

    public DisplaySettingsForm(SettingsStore settings, IEnumerable<string>? observedSources = null)
    {
        this.settings = settings;
        Text = "显示来源与状态文本";
        Size = new Size(720, 760);
        MinimumSize = new Size(560, 600);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9);

        var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var sourceDefinitions = AgentStatusSettings.SourceDefinitions.Concat((observedSources ?? []).Concat(settings.Current.EnabledSources.Keys)
            .Except(AgentStatusSettings.SourceDefinitions.Select(s => s.Id), StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(id => (Id: id, Label: StateStore.DisplaySource(id)))).ToArray();
        var sources = new GroupBox { Text = "显示哪些工具", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var sourceTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = (sourceDefinitions.Length + 1) / 2 + 1 };
        sourceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sourceTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (var i = 0; i < sourceDefinitions.Length; i++)
        {
            if (i % 2 == 0) sourceTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var definition = sourceDefinitions[i];
            var check = new CheckBox { Text = definition.Label, AutoSize = true, Margin = new Padding(4, 5, 12, 5), Tag = definition.Id };
            sourceBoxes[definition.Id] = check;
            sourceTable.Controls.Add(check, i % 2, i / 2);
        }
        sourceTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var reportedOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        reportedOptions.Controls.Add(showReported);
        sourceTable.Controls.Add(reportedOptions, 0, (sourceDefinitions.Length + 1) / 2);
        sourceTable.SetColumnSpan(reportedOptions, 2);
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

        var note = new Label { Text = "工具勾选仅控制采集；主动上报独立显示，与采集共用完成、提醒和超时设置。关闭显示不会停止后台接收。", AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(6, 10, 6, 8) };
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
        showReported.Checked = config.ShowReportedStates;
        foreach (var pair in sourceBoxes) pair.Value.Checked = config.EnabledSources.GetValueOrDefault(pair.Key, true);
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
            foreach (var pair in sourceBoxes) enabled[pair.Key] = pair.Value.Checked;
            var texts = new Dictionary<string, string>(current.DisplayTexts, StringComparer.OrdinalIgnoreCase);
            foreach (var definition in AgentStatusSettings.DisplayDefinitions) texts[definition.Key] = textBoxes[definition.Key].Text;
            settings.Save(current with { EnabledSources = enabled, DisplayTexts = texts, ShowReportedStates = showReported.Checked });
            status.Text = "已保存，任务栏会自动更新";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            status.Text = "保存失败，请检查配置目录权限";
        }
    }
}
