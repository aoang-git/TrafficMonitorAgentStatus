using System.Text.Json;

namespace AgentStatus;

public sealed record AgentStatusSettings
{
    public bool ShowSource { get; init; } = true;
    public int CompletionToastMs { get; init; } = 5000;
    public int MaxWorkingMs { get; init; } = 10 * 60 * 1000;
    public int CodexPollIntervalMs { get; init; } = 750;

    public static AgentStatusSettings Defaults => new();

    public AgentStatusSettings Normalize() => this with
    {
        CompletionToastMs = Math.Clamp(CompletionToastMs, 0, 60 * 1000),
        // A finite lower bound is intentional: silent work must not become an
        // immortal task-bar state when a source loses its stop event.
        MaxWorkingMs = Math.Clamp(MaxWorkingMs, 30 * 1000, 2 * 60 * 60 * 1000),
        CodexPollIntervalMs = Math.Clamp(CodexPollIntervalMs, 200, 3000)
    };
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
