using System.Text.Json;
namespace AgentStatus;

public sealed record AgentStatusSettings
{
    public int ConfigVersion { get; init; } = 2;
    public string RuntimeDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentStatusRuntime");
    public bool ShowSource { get; init; } = true;
    public bool ShowReportedStates { get; init; } = true;
    public int ReportedStateLeaseMs { get; init; } = 60000;
    public int CompletionToastMs { get; init; } = 5000;
    public int NoticeToastMs { get; init; } = 15000;
    public int MaxWorkingMs { get; init; } = 5 * 60 * 1000;
    public int MaxWaitingMs { get; init; } = 30 * 60 * 1000;
    public Dictionary<string, bool> EnabledSources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DisplayTexts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public static readonly IReadOnlyList<(string Id, string Label)> SourceDefinitions =
        [("codex", "Codex"), ("claude", "Claude"), ("deepseek", "DeepSeek"), ("pi", "Pi")];
    public static readonly IReadOnlyList<(string Key, string Label, string DefaultText)> DisplayDefinitions =
    [
        ("working", "工作中", "工作中"), ("tool", "调用工具", "调用工具"), ("waiting", "待确认", "待确认"),
        ("taskCompleted", "任务完成", "任务完成"), ("taskInterrupted", "任务中断", "任务中断"), ("unknown", "状态未知", "状态未知")
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
            foreach (var pair in EnabledSources.Take(256))
                if (pair.Key.Length is > 0 and <= 256 && !pair.Key.Any(char.IsControl)) enabled[pair.Key] = pair.Value;
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var display in DisplayDefinitions)
        {
            var clean = new string((DisplayTexts?.GetValueOrDefault(display.Key) ?? "").Where(c => !char.IsControl(c)).Take(32).ToArray()).Trim();
            texts[display.Key] = clean.Length > 0 ? clean : display.DefaultText;
        }
        string directory = string.IsNullOrWhiteSpace(RuntimeDirectory) ? Defaults.RuntimeDirectory : Path.GetFullPath(RuntimeDirectory.Trim());
        return this with { ConfigVersion = 2, RuntimeDirectory = directory, CompletionToastMs = Math.Clamp(CompletionToastMs, 0, 60000),
            ReportedStateLeaseMs = Math.Clamp(ReportedStateLeaseMs, 1000, 3600000), NoticeToastMs = Math.Clamp(NoticeToastMs, 1000, 600000), MaxWorkingMs = Math.Clamp(MaxWorkingMs, 30000, 7200000),
            MaxWaitingMs = Math.Clamp(MaxWaitingMs, 60000, 86400000), EnabledSources = enabled, DisplayTexts = texts };
    }
}

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    readonly object gate = new();
    readonly string path;
    AgentStatusSettings current = AgentStatusSettings.Defaults;
    DateTime lastWriteUtc = DateTime.MinValue;
    long lastLength = -1;
    public SettingsStore(string? path = null) { this.path = path ?? System.IO.Path.Combine(Names.DataDir, "config.json"); }
    public string Path => path;
    public AgentStatusSettings Current
    {
        get
        {
            lock (gate)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length <= 65536 && (info.LastWriteTimeUtc != lastWriteUtc || info.Length != lastLength))
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(path));
                        if (!json.RootElement.TryGetProperty("configVersion", out var version) || version.GetInt32() != 2)
                            throw new InvalidDataException("Unsupported settings version");
                        current = json.RootElement.Deserialize<AgentStatusSettings>(JsonOptions)!.Normalize();
                        lastWriteUtc = info.LastWriteTimeUtc; lastLength = info.Length;
                    }
                }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
                return current;
            }
        }
    }
    public void Save(AgentStatusSettings settings)
    {
        lock (gate)
        {
            var normalized = settings.Normalize();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
                File.Move(temporary, path, true); current = normalized;
                var info = new FileInfo(path); lastWriteUtc = info.LastWriteTimeUtc; lastLength = info.Length;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public void ResetToDefaults() => Save(AgentStatusSettings.Defaults);
}
