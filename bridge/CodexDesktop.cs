using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            if (callId.Length > 0 && name.Length > 0) tools[callId] = name;
            if (name.Equals("request_user_input", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(Event("input.requested", ordinal, callType, turn, occurredAt, name, callId, callId));
            }
            else if (RequiresPermission(payload))
            {
                events.Add(Event("permission.requested", ordinal, callType, turn, occurredAt, name, callId, callId));
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

    static bool RequiresPermission(JsonElement payload)
    {
        if (!payload.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.String) return false;
        var text = input.GetString() ?? "";
        return text.Contains("require_escalated", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sandbox_permissions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("justification", StringComparison.OrdinalIgnoreCase);
    }

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

public sealed class CodexDesktopWatcher(StateStore state)
{
    const int InitialTailBytes = 512 * 1024;
    readonly string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    readonly Dictionary<string, Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Scan(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { Diagnostics.Write("codex-desktop", e.GetType().Name); }
            try { await Task.Delay(750, ct); }
            catch (OperationCanceledException) { }
        }
    }

    void Scan()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            if (!cursors.TryGetValue(file, out var cursor))
            {
                cursor = new Cursor(file, Math.Max(0, info.Length - InitialTailBytes), state.Apply);
                cursors[file] = cursor;
                if (!ReadFirstMetadata(cursor)) continue;
                cursor.Initializing = true;
            }
            if (!cursor.IsDesktop) continue;
            ReadAppended(cursor);
            if (cursor.Initializing) FinishInitialReplay(cursor);
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

    static void ReadAppended(Cursor cursor)
    {
        try
        {
            using var stream = new FileStream(cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < cursor.Offset) cursor.Reset(stream.Length);
            stream.Position = cursor.Offset;
            var buffer = new byte[32 * 1024];
            var read = 0;
            using var appended = new MemoryStream();
            if (cursor.Pending.Length > 0) appended.Write(cursor.Pending, 0, cursor.Pending.Length);
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) appended.Write(buffer, 0, read);
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
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void FinishInitialReplay(Cursor cursor)
    {
        cursor.Initializing = false;
        var start = cursor.Buffered.FindLastIndex(x => x.Event.EventType == "turn.started");
        if (start < 0)
        {
            cursor.Buffered.Clear();
            return;
        }
        var terminal = cursor.Buffered.FindLastIndex(x => x.Event.EventType is "turn.finished" or "turn.failed");
        if (terminal > start)
        {
            cursor.Buffered.Clear();
            return;
        }
        foreach (var item in cursor.Buffered.Skip(start)) cursor.Apply(item.Event);
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
        public bool IsDesktop => Parser.IsDesktop;
        public List<BufferedEvent> Buffered { get; } = [];
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
                if (Initializing) Buffered.Add(item);
                else apply(e);
            }
        }
    }

    sealed record BufferedEvent(StatusEvent Event);
}
