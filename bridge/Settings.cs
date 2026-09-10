using System.Text.Json;

namespace AgentStatus;

public sealed record AgentStatusSettings
{
    public bool ShowSource { get; init; } = true;
    public int CompletionToastMs { get; init; } = 5000;
    // How long an unknown or failed state stays on the taskbar. Deliberately
    // longer than the completion toast so it is noticed, but bounded so it can
    // never occupy the display permanently. Always kept strictly above
    // CompletionToastMs by Normalize().
    public int NoticeToastMs { get; init; } = 15000;
    public int MaxWorkingMs { get; init; } = 10 * 60 * 1000;
    public int MaxWaitingMs { get; init; } = 2 * 60 * 60 * 1000;
    public int CodexPollIntervalMs { get; init; } = 750;
    public Dictionary<string, bool> EnabledSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DisplayTexts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<(string Id, string Label)> SourceDefinitions =
    [
        ("claude-code", "Claude Code"),
        ("codex-desktop", "Codex 桌面版"),
        ("codex-cli", "Codex CLI / TUI"),
        ("cursor", "Cursor"),
        ("gemini-cli", "Gemini CLI"),
        ("generic", "通用来源")
    ];

    public static readonly IReadOnlyList<(string Key, string Label, string DefaultText)> DisplayDefinitions =
    [
        ("working", "工作中", "工作中"),
        ("tool", "调用工具", "调用工具"),
        ("permission", "待授权", "待授权"),
        ("input", "待回答", "待回答"),
        ("turnCompleted", "本轮完成", "本轮完成"),
        ("taskCompleted", "任务完成", "任务完成"),
        ("error", "异常", "异常"),
        ("unknown", "状态未知", "状态未知")
    ];

    public static AgentStatusSettings Defaults => new()
    {
        EnabledSources = SourceDefinitions.ToDictionary(x => x.Id, _ => true, StringComparer.OrdinalIgnoreCase),
        DisplayTexts = DisplayDefinitions.ToDictionary(x => x.Key, x => x.DefaultText, StringComparer.OrdinalIgnoreCase)
    };

    public AgentStatusSettings Normalize()
    {
        var enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in SourceDefinitions) enabled[source.Id] = EnabledSources?.GetValueOrDefault(source.Id, true) ?? true;
        if (EnabledSources != null)
            foreach (var pair in EnabledSources) if (!enabled.ContainsKey(pair.Key)) enabled[pair.Key] = pair.Value;

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var display in DisplayDefinitions) texts[display.Key] = CleanText(DisplayTexts?.GetValueOrDefault(display.Key), display.DefaultText);
        if (DisplayTexts != null)
            foreach (var pair in DisplayTexts)
                if (!texts.ContainsKey(pair.Key)) texts[pair.Key] = CleanText(pair.Value, pair.Key);

        var completionMs = Math.Clamp(CompletionToastMs, 0, 60 * 1000);
        // An unknown/error notice outlives the completion toast so it is
        // actually noticed, and the 10-minute ceiling keeps a bad config from
        // parking a stale state on the taskbar indefinitely.
        var noticeMs = Math.Clamp(NoticeToastMs, completionMs + 1000, 10 * 60 * 1000);

        return this with
        {
            CompletionToastMs = completionMs,
            NoticeToastMs = noticeMs,
        // A finite lower bound is intentional: silent work must not become an
        // immortal task-bar state when a source loses its stop event.
            MaxWorkingMs = Math.Clamp(MaxWorkingMs, 30 * 1000, 2 * 60 * 60 * 1000),
            // A lost answer/approval resolution event must not leave the
            // taskbar occupied forever. Keep the default generous because a
            // real user may intentionally leave a prompt waiting for hours.
            MaxWaitingMs = Math.Clamp(MaxWaitingMs, 60 * 1000, 24 * 60 * 60 * 1000),
            CodexPollIntervalMs = Math.Clamp(CodexPollIntervalMs, 200, 3000),
            EnabledSources = enabled,
            DisplayTexts = texts
        };
    }

    static string CleanText(string? value, string fallback)
    {
        var clean = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length == 0 ? fallback : new string(clean.Take(32).ToArray());
    }
}

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    readonly object gate = new();
    readonly string path;
    AgentStatusSettings current = AgentStatusSettings.Defaults;
    DateTime lastWriteUtc = DateTime.MinValue;
    long lastLength = -1;
    bool loaded;

    public SettingsStore(string? path = null)
    {
        this.path = path ?? System.IO.Path.Combine(Names.DataDir, "config.json");
    }

    public string Path => path;

    public AgentStatusSettings Current
    {
        get
        {
            RefreshIfChanged();
            lock (gate) return current;
        }
    }

    public void Save(AgentStatusSettings settings)
    {
        var normalized = settings.Normalize();
        var directory = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Settings directory is missing");
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporary, path, true);
            lock (gate)
            {
                current = normalized;
                loaded = true;
                var info = new FileInfo(path);
                lastWriteUtc = info.LastWriteTimeUtc;
                lastLength = info.Length;
            }
        }
        finally
        {
            // The temporary file is a single known path. Do not scan or remove
            // any other files if saving fails.
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public void ResetToDefaults() => Save(AgentStatusSettings.Defaults);

    void RefreshIfChanged()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                lock (gate)
                {
                    if (!loaded) { current = AgentStatusSettings.Defaults; loaded = true; }
                }
                return;
            }
            lock (gate)
            {
                if (loaded && info.LastWriteTimeUtc == lastWriteUtc && info.Length == lastLength) return;
            }
            var parsed = JsonSerializer.Deserialize<AgentStatusSettings>(File.ReadAllText(path), JsonOptions)?.Normalize() ?? AgentStatusSettings.Defaults;
            lock (gate)
            {
                current = parsed;
                loaded = true;
                lastWriteUtc = info.LastWriteTimeUtc;
                lastLength = info.Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A malformed or temporarily locked file must not stop the status
            // receiver. Keep the last valid settings, or use defaults initially.
            lock (gate)
            {
                if (!loaded) { current = AgentStatusSettings.Defaults; loaded = true; }
            }
        }
    }
}
