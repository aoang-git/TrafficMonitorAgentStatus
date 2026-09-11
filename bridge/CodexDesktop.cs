using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace AgentStatus;

// The desktop app records one JSON object per line in the user's Codex session
// directory.  This adapter consumes only event metadata; message/tool payloads
// are never copied into the bridge or its diagnostics log.
public sealed class CodexDesktopParser
{
    static readonly Regex SessionIdFromFile = new(
        @"(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    readonly Dictionary<string, string> tools = [];
    readonly Queue<string> toolOrder = [];
    const int MaxTrackedTools = 1024;
    public string FilePath { get; }
    public string SessionId { get; private set; }
    public string ProjectLabel { get; private set; } = "";
    public string SourceVersion { get; private set; } = "";
    public bool IsDesktop { get; private set; }
    public string? CurrentTurn { get; private set; }

    public CodexDesktopParser(string filePath)
    {
        FilePath = filePath;
        var match = SessionIdFromFile.Match(Path.GetFileNameWithoutExtension(filePath));
        SessionId = match.Success ? match.Groups["id"].Value : Path.GetFileNameWithoutExtension(filePath);
    }

    public void ReadMetadata(string line)
    {
        if (!TryRead(line, out var root) || !IsType(root, "session_meta") || !root.TryGetProperty("payload", out var payload)) return;
        SessionId = StringValue(payload, "session_id") is { Length: > 0 } session ? session : SessionId;
        SourceVersion = StringValue(payload, "cli_version");
        ProjectLabel = Leaf(StringValue(payload, "cwd"));
        var originator = StringValue(payload, "originator");
        var threadSource = StringValue(payload, "thread_source");
        IsDesktop = originator.Equals("Codex Desktop", StringComparison.OrdinalIgnoreCase)
            || originator.Equals("codex_work_desktop", StringComparison.OrdinalIgnoreCase)
            || threadSource.Equals("user", StringComparison.OrdinalIgnoreCase) && originator.Contains("desktop", StringComparison.OrdinalIgnoreCase);
    }

    public List<StatusEvent> Parse(string line, long ordinal)
    {
        if (!TryRead(line, out var root) || !root.TryGetProperty("payload", out var payload)) return [];
        var topType = StringValue(root, "type");
        var eventType = StringValue(payload, "type");
        var turn = StringValue(payload, "turn_id");
        var occurredAt = DateTimeOffset.TryParse(StringValue(root, "timestamp"), out var timestamp) ? timestamp : DateTimeOffset.UtcNow;
        var events = new List<StatusEvent>();

        if (topType == "event_msg")
        {
            if (eventType == "task_started")
            {
                CurrentTurn = turn.Length > 0 ? turn : CurrentTurn;
                events.Add(Event("turn.started", ordinal, "task_started", turn, occurredAt));
            }
            else if (eventType == "task_complete")
            {
                events.Add(Event("turn.finished", ordinal, "task_complete", turn, occurredAt));
            }
            else if (eventType == "turn_aborted")
            {
                events.Add(Event("turn.failed", ordinal, "turn_aborted", turn, occurredAt));
            }
            return events;
        }

        if (topType != "response_item") return events;
        var callType = eventType;
        if (callType is "custom_tool_call" or "function_call")
        {
            var callId = StringValue(payload, "call_id");
            if (callId.Length == 0) callId = StringValue(payload, "id");
            var name = StringValue(payload, "name");
            if (callId.Length > 0 && name.Length > 0)
            {
                if (!tools.ContainsKey(callId)) toolOrder.Enqueue(callId);
                tools[callId] = name;
                while (toolOrder.Count > MaxTrackedTools) tools.Remove(toolOrder.Dequeue());
            }
            if (name.Equals("request_user_input", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(Event("input.requested", ordinal, callType, turn, occurredAt, name, callId, callId));
            }
            else
            {
                events.Add(Event("tool.started", ordinal, callType, turn, occurredAt, name, callId));
            }
        }
        else if (callType is "custom_tool_call_output" or "function_call_output")
        {
            var callId = StringValue(payload, "call_id");
            if (callId.Length == 0) callId = StringValue(payload, "id");
            var name = callId.Length > 0 ? tools.GetValueOrDefault(callId, "") : "";
            if (name.Equals("request_user_input", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(Event("input.resolved", ordinal, callType, turn, occurredAt, name, callId, callId));
            }
            else
            {
                events.Add(Event("tool.finished", ordinal, callType, turn, occurredAt, name, callId));
            }
        }
        return events;
    }

    StatusEvent Event(string type, long ordinal, string rawName, string turn, DateTimeOffset occurredAt, string tool = "", string? call = null, string? request = null) => new()
    {
        EventId = $"{SessionId}:{ordinal}:{type}",
        Source = "codex-desktop",
        SourceVersion = SourceVersion,
        InstanceId = "desktop",
        SessionId = SessionId,
        TurnId = turn.Length > 0 ? turn : CurrentTurn,
        EventType = type,
        RawEventName = rawName,
        Sequence = ordinal,
        ProjectLabel = ProjectLabel,
        ToolName = tool,
        ToolCallId = call,
        RequestId = request,
        OccurredAt = occurredAt
    };

    static bool TryRead(string line, out JsonElement root)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    static bool IsType(JsonElement value, string expected) => StringValue(value, "type") == expected;

    static string StringValue(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    static string Leaf(string path)
    {
        if (path.Length == 0) return "";
        return path.Replace('\\', '/').TrimEnd('/').Split('/').LastOrDefault() ?? "";
    }
}

public sealed class CodexDesktopWatcher
{
    const int RecoveryTailBytes = 8 * 1024 * 1024;
    const int MaxReadBytesPerScan = 1024 * 1024;
    const int MaxPendingLineBytes = 64 * 1024;
    const int MaxCursors = 256;
    const int MaxDiscoveryFiles = 32;
    const int MaxFilesPerScan = 4;
    const int MaxRecoveryEvents = 4096;
    const long DiscoveryIntervalMs = 60 * 1000;
    const long CursorIdleMs = 24 * 60 * 60 * 1000;
    readonly StateStore state;
    readonly SettingsStore settings;
    readonly string root;
    readonly Dictionary<string, Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> dirty = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? watcher;
    long nextDiscovery;

    public CodexDesktopWatcher(StateStore state, SettingsStore? settings = null, string? root = null)
    {
        this.state = state;
        this.settings = settings ?? new SettingsStore();
        this.root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    }

    public int TrackedFiles => cursors.Count;

    public async Task Run(CancellationToken ct)
    {
        // Let Server.Run create its pipe workers and snapshot timer before the
        // first bounded recovery scan performs synchronous file I/O.
        await Task.Yield();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TryStartWatcher();
                try { ScanOnce(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { Diagnostics.Write("codex-desktop", e.GetType().Name); }
                try { await Task.Delay(settings.Current.CodexPollIntervalMs, ct); }
                catch (OperationCanceledException) { }
            }
        }
        finally { watcher?.Dispose(); }
    }

    void TryStartWatcher()
    {
        if (watcher != null || !Directory.Exists(root)) return;
        try
        {
            watcher = new FileSystemWatcher(root, "rollout-*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += (_, e) => dirty[e.FullPath] = 0;
            watcher.Changed += (_, e) => dirty[e.FullPath] = 0;
            watcher.Renamed += (_, e) => dirty[e.FullPath] = 0;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            watcher?.Dispose();
            watcher = null;
            Diagnostics.Write("codex-watcher", e.GetType().Name);
        }
    }

    public void ScanOnce()
    {
        if (!Directory.Exists(root)) return;
        var timestamp = Environment.TickCount64;
        if (timestamp >= nextDiscovery)
        {
            DiscoverRecent();
            nextDiscovery = timestamp + DiscoveryIntervalMs;
        }
        foreach (var path in dirty.Keys.ToArray())
            if (dirty.TryRemove(path, out _)) EnsureCursor(path, timestamp);

        var processed = 0;
        foreach (var pair in cursors.ToArray())
        {
            var cursor = pair.Value;
            var info = new FileInfo(cursor.Path);
            if (!info.Exists) { cursors.Remove(pair.Key); continue; }
            if (cursor.Offset >= info.Length && info.Length == cursor.ObservedLength && info.LastWriteTimeUtc == cursor.LastWriteUtc) continue;
            if (processed >= MaxFilesPerScan) continue;
            processed++;
            cursor.ObservedLength = info.Length;
            cursor.LastWriteUtc = info.LastWriteTimeUtc;
            cursor.LastActivity = timestamp;
            if (!cursor.MetadataConfirmed) cursor.MetadataConfirmed = ReadFirstMetadata(cursor);
            if (!cursor.IsDesktop) continue;
            if (!cursor.Initializing && cursor.Offset == 0 && info.Length > 0) cursor.Initializing = true;
            var caughtUp = ReadAppended(cursor, MaxReadBytesPerScan);
            if (cursor.Initializing && caughtUp) FinishInitialReplay(cursor);
        }
        EvictCursors(timestamp);
    }

    void DiscoverRecent()
    {
        var cutoff = DateTime.UtcNow.AddDays(-2);
        var recent = Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists && info.LastWriteTimeUtc >= cutoff)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(MaxDiscoveryFiles)
            .ToArray();
        var timestamp = Environment.TickCount64;
        foreach (var info in recent) EnsureCursor(info.FullName, timestamp);
    }

    void EnsureCursor(string path, long timestamp)
    {
        if (!Path.GetFileName(path).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
        if (cursors.ContainsKey(path)) return;
        var info = new FileInfo(path);
        if (!info.Exists) return;
        if (cursors.Count >= MaxCursors)
        {
            var oldest = cursors.Values.OrderBy(x => x.LastWriteUtc).FirstOrDefault();
            if (oldest != null) cursors.Remove(oldest.Path);
        }
        cursors[path] = new Cursor(path, Math.Max(0, info.Length - RecoveryTailBytes), state.Apply)
        {
            Initializing = true,
            LastActivity = timestamp,
            LastWriteUtc = DateTime.MinValue,
            ObservedLength = -1
        };
    }

    void EvictCursors(long timestamp)
    {
        foreach (var cursor in cursors.Values.Where(x => timestamp - x.LastActivity > CursorIdleMs).ToArray()) cursors.Remove(cursor.Path);
        while (cursors.Count > MaxCursors)
        {
            var oldest = cursors.Values.OrderBy(x => x.LastWriteUtc).First();
            cursors.Remove(oldest.Path);
        }
    }

    static bool ReadFirstMetadata(Cursor cursor)
    {
        try
        {
            using var stream = new FileStream(cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: false);
            for (var i = 0; i < 16 && !reader.EndOfStream; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                cursor.Parser.ReadMetadata(line);
                if (cursor.Parser.IsDesktop) return true;
            }
            return cursor.Parser.IsDesktop;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static bool ReadAppended(Cursor cursor, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < cursor.Offset) cursor.Reset(stream.Length);
            stream.Position = cursor.Offset;
            var buffer = new byte[32 * 1024];
            var read = 0;
            var remaining = maxBytes;
            using var appended = new MemoryStream();
            if (cursor.Pending.Length > 0) appended.Write(cursor.Pending, 0, cursor.Pending.Length);
            while (remaining > 0 && (read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining))) > 0)
            {
                appended.Write(buffer, 0, read);
                remaining -= read;
            }
            cursor.Offset = stream.Position;
            var bytes = appended.ToArray();
            var start = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                if (!cursor.DiscardUntilNewline)
                {
                    var length = i - start;
                    var line = Encoding.UTF8.GetString(bytes, start, length).TrimEnd('\r');
                    if (line.Length > 0) cursor.Accept(line);
                }
                else cursor.DiscardUntilNewline = false;
                start = i + 1;
            }
            cursor.Pending = bytes[start..];
            if (cursor.Pending.Length > MaxPendingLineBytes)
            {
                cursor.Pending = [];
                cursor.DiscardUntilNewline = true;
            }
            return cursor.Offset >= stream.Length;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static void FinishInitialReplay(Cursor cursor)
    {
        cursor.Initializing = false;
        foreach (var item in cursor.Buffered) cursor.Apply(item.Event);
        cursor.Buffered.Clear();
    }

    sealed class Cursor
    {
        public string Path { get; }
        public CodexDesktopParser Parser { get; }
        public long Offset { get; set; }
        public byte[] Pending { get; set; } = [];
        public bool DiscardUntilNewline { get; set; }
        public bool Initializing { get; set; }
        public bool MetadataConfirmed { get; set; }
        public bool IsDesktop => Parser.IsDesktop;
        public List<BufferedEvent> Buffered { get; } = [];
        public long LastActivity { get; set; }
        public long ObservedLength { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public bool RecoveryOverflow { get; set; }
        long ordinal;

        readonly Func<StatusEvent, bool> apply;

        public Cursor(string path, long offset, Func<StatusEvent, bool> apply)
        {
            Path = path;
            Offset = offset;
            DiscardUntilNewline = offset > 0;
            Parser = new CodexDesktopParser(path);
            this.apply = apply;
        }

        public void Reset(long length)
        {
            Offset = length;
            Pending = [];
            DiscardUntilNewline = false;
        }

        public void Apply(StatusEvent e) => apply(e);

        public void Accept(string line)
        {
            ordinal++;
            Parser.ReadMetadata(line);
            foreach (var e in Parser.Parse(line, ordinal))
            {
                var item = new BufferedEvent(e);
                if (Initializing)
                {
                    if (e.EventType == "turn.started")
                    {
                        Buffered.Clear();
                        RecoveryOverflow = false;
                        Buffered.Add(item);
                    }
                    else if (e.EventType is "turn.finished" or "turn.failed")
                    {
                        Buffered.Clear();
                        RecoveryOverflow = false;
                    }
                    else if (Buffered.Count > 0 && !RecoveryOverflow)
                    {
                        if (Buffered.Count < MaxRecoveryEvents) Buffered.Add(item);
                        else { Buffered.Clear(); RecoveryOverflow = true; }
                    }
                }
                else apply(e);
            }
        }
    }

    sealed record BufferedEvent(StatusEvent Event);
}
