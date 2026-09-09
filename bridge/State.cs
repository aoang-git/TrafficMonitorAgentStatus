using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStatus;

public sealed record StatusEvent
{
    public int SchemaVersion { get; init; } = 1;
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string Source { get; init; } = "generic";
    public string SourceVersion { get; init; } = "";
    public string InstanceId { get; init; } = "local";
    public string SessionId { get; init; } = "";
    public string? TurnId { get; init; }
    public string? AgentId { get; init; }
    public string? ToolCallId { get; init; }
    public string? RequestId { get; init; }
    public string EventType { get; init; } = "";
    public string RawEventName { get; init; } = "";
    public long? Sequence { get; init; }
    public string ProjectLabel { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string? TaskId { get; init; }
    public bool IsMainGoal { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class Session
{
    public string Key { get; init; } = "";
    public string Source { get; init; } = "";
    public string Project { get; set; } = "";
    public string SessionId { get; init; } = "";
    public string? Turn { get; set; }
    public HashSet<string> OldTurns { get; } = [];
    public Dictionary<string, string> Tools { get; } = [];
    public HashSet<string> FinishedTools { get; } = [];
    public Dictionary<string, string> Requests { get; } = [];
    // PermissionRequest does not carry tool_use_id. Keep the tool name so the
    // following PreToolUse/PostToolUse event can close the pending request.
    public Dictionary<string, string> RequestTools { get; } = [];
    public HashSet<string> ResolvedRequests { get; } = [];
    public bool Working { get; set; }
    public string Terminal { get; set; } = "idle";
    public string LastNotice { get; set; } = "";
    public long LastSeen { get; set; }
    public long CompletedAt { get; set; } = -1;
    public DateTimeOffset UpdatedAt { get; set; }
    public Dictionary<string, long> Sequences { get; } = [];
    public string DisplayState => Requests.Values.Contains("permission") ? "待授权" : Requests.Values.Contains("input") ? "待回答" : Terminal == "error" ? "异常" : Tools.Count > 0 ? "调用工具" : Working ? "工作中" : Terminal switch { "turn" => "本轮完成", "goal" => "任务完成", "ended" => "会话结束", "unknown" => "状态未知", _ => "空闲" };
}

public sealed record SessionView(string Key, string Source, string Project, string SessionId, string State, int Tools, int Permissions, int Questions, DateTimeOffset UpdatedAt, string Note);
public sealed record Snapshot(string Text, string Tooltip, List<SessionView> Sessions, long Accepted, long Rejected, Dictionary<string, long> EventCounts);

public static class JsonWire
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, WriteIndented = false };
}

public sealed class StateStore(Func<long>? clock = null)
{
    readonly object gate = new();
    readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    readonly Dictionary<string, Session> sessions = [];
    readonly HashSet<string> ids = [];
    readonly Queue<string> idOrder = [];
    long accepted, rejected;
    readonly Dictionary<string, long> counts = [];
    const int MaxSessions = 128, MaxIds = 8192, MaxEntries = 256;
    const long MaxWorkingMs = 10 * 60 * 1000;
    const long CompletionToastMs = 5000;
    public static readonly HashSet<string> Types = ["session.started", "session.ended", "turn.started", "execution.resumed", "tool.started", "tool.finished", "tool.failed", "input.requested", "input.resolved", "permission.requested", "permission.resolved", "turn.finished", "turn.failed", "task.completed", "session.unknown"];

    static string DisplaySource(string source) => source switch
    {
        "claude-code" => "Claudecode",
        "codex-desktop" => "Codex",
        "cursor" => "Cursor",
        "gemini-cli" => "Gemini",
        _ => source
    };

    static bool IsActive(string state) => state is "待授权" or "待回答" or "异常" or "调用工具" or "工作中" or "状态未知";

    static void RemoveRequest(Session s, string key)
    {
        s.Requests.Remove(key);
        s.RequestTools.Remove(key);
    }

    static void ResolvePermissionForTool(Session s, string stream, string toolName)
    {
        var prefix = stream + ":";
        var pending = s.Requests
            .Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && x.Value == "permission")
            .Where(x => s.RequestTools.GetValueOrDefault(x.Key) == toolName)
            .OrderBy(x => x.Key == prefix + "unassociated" ? 0 : 1)
            .Select(x => x.Key)
            .FirstOrDefault();
        if (pending != null) RemoveRequest(s, pending);
    }

    static string Clean(string value, int max = 160) => new(value.Where(c => !char.IsControl(c)).Take(max).ToArray());
    public bool Apply(StatusEvent e)
    {
        lock (gate)
        {
            if (e == null || e.SchemaVersion != 1 || e.EventType == null || !Types.Contains(e.EventType) || string.IsNullOrWhiteSpace(e.SessionId) || string.IsNullOrWhiteSpace(e.EventId) || e.EventId.Length > 256 || e.SessionId.Length > 512 || string.IsNullOrWhiteSpace(e.Source) || e.Source.Length > 64 || string.IsNullOrWhiteSpace(e.InstanceId) || e.InstanceId.Length > 256 || e.ProjectLabel == null || e.ToolName == null || (e.ToolCallId?.Length ?? 0) > 512 || (e.RequestId?.Length ?? 0) > 512 || (e.TurnId?.Length ?? 0) > 512 || (e.AgentId?.Length ?? 0) > 256) { rejected++; return false; }
            var identity = JsonSerializer.Serialize(new[] { e.Source, e.InstanceId, e.EventId });
            if (!ids.Add(identity)) return true;
            idOrder.Enqueue(identity);
            if (idOrder.Count > MaxIds) ids.Remove(idOrder.Dequeue());
            var key = JsonSerializer.Serialize(new[] { e.Source, e.InstanceId, e.SessionId });
            if (!sessions.TryGetValue(key, out var s))
            {
                if (sessions.Count == MaxSessions)
                {
                    var old = sessions.Values.Where(x => !x.Working && x.Requests.Count == 0 && x.Tools.Count == 0).OrderBy(x => x.LastSeen).FirstOrDefault();
                    if (old == null) { rejected++; return false; }
                    sessions.Remove(old.Key);
                }
                sessions[key] = s = new() { Key = key, Source = Clean(e.Source), SessionId = Clean(e.SessionId), Project = Clean(e.ProjectLabel) };
            }
            var stream = e.AgentId ?? "main";
            if (e.Sequence is long seq)
            {
                if (s.Sequences.TryGetValue(stream, out var last) && seq <= last) return true;
                if (s.Sequences.Count < MaxEntries || s.Sequences.ContainsKey(stream)) s.Sequences[stream] = seq;
            }
            if (e.TurnId != null && s.OldTurns.Contains(e.TurnId)) return true;
            if (e.EventType == "turn.started")
            {
                if (s.Turn != null && s.Turn != e.TurnId) { s.OldTurns.Add(s.Turn); if (s.OldTurns.Count > MaxEntries) s.OldTurns.Remove(s.OldTurns.First()); }
                s.Turn = e.TurnId;
                s.Tools.Clear(); s.FinishedTools.Clear(); s.Requests.Clear(); s.RequestTools.Clear(); s.ResolvedRequests.Clear();
            }
            else if (e.TurnId != null && s.Turn != null && s.Turn != e.TurnId) return true;
            if (e.ProjectLabel.Length > 0) s.Project = Clean(e.ProjectLabel);
            s.LastSeen = now(); s.UpdatedAt = DateTimeOffset.UtcNow;
            string call = stream + ":" + (e.ToolCallId ?? e.ToolName);
            string req = stream + ":" + (e.RequestId ?? e.ToolCallId ?? "unassociated");
            switch (e.EventType)
            {
                case "session.started": break;
                case "turn.started": case "execution.resumed": s.Working = true; s.Terminal = "idle"; s.CompletedAt = -1; break;
                case "tool.started":
                    ResolvePermissionForTool(s, stream, e.ToolName);
                    if (!s.FinishedTools.Contains(call) && s.Tools.Count < MaxEntries) s.Tools[call] = Clean(e.ToolName);
                    s.Working = true; s.Terminal = "idle"; s.CompletedAt = -1; break;
                case "tool.finished": case "tool.failed":
                    s.Tools.Remove(call);
                    if (s.FinishedTools.Count < MaxEntries) s.FinishedTools.Add(call);
                    RemoveRequest(s, req);
                    ResolvePermissionForTool(s, stream, e.ToolName);
                    if (e.EventType == "tool.failed") s.LastNotice = "工具失败；不代表整个回合失败";
                    break;
                case "input.requested": case "permission.requested":
                    if (!s.ResolvedRequests.Contains(req) && s.Requests.Count < MaxEntries)
                    {
                        s.Requests[req] = e.EventType == "input.requested" ? "input" : "permission";
                        s.RequestTools[req] = Clean(e.ToolName, 160);
                    }
                    break;
                case "input.resolved": case "permission.resolved":
                    RemoveRequest(s, req);
                    if (e.EventType == "permission.resolved") ResolvePermissionForTool(s, stream, e.ToolName);
                    if (s.ResolvedRequests.Count < MaxEntries) s.ResolvedRequests.Add(req);
                    break;
                case "turn.finished":
                    if (e.AgentId == null) { s.Working = false; s.Tools.Clear(); s.Requests.Clear(); s.RequestTools.Clear(); s.Terminal = "turn"; s.CompletedAt = now(); }
                    break;
                case "task.completed":
                    s.LastNotice = e.IsMainGoal ? "主目标完成" : "子任务完成";
                    if (e.IsMainGoal && e.AgentId == null) { s.Working = false; s.Tools.Clear(); s.Requests.Clear(); s.RequestTools.Clear(); s.Terminal = "goal"; s.CompletedAt = now(); }
                    break;
                case "turn.failed": s.Working = false; s.Tools.Clear(); s.Requests.Clear(); s.RequestTools.Clear(); s.Terminal = "error"; break;
                case "session.ended": s.Working = false; s.Tools.Clear(); s.Requests.Clear(); s.RequestTools.Clear(); s.Terminal = "ended"; break;
                case "session.unknown": s.Working = false; s.Tools.Clear(); s.Requests.Clear(); s.RequestTools.Clear(); s.Terminal = "unknown"; break;
            }
            accepted++;
            string counter = Clean(e.Source, 64) + ":" + Clean(e.RawEventName.Length > 0 ? e.RawEventName : e.EventType, 64);
            if (counts.Count < 256 || counts.ContainsKey(counter)) counts[counter] = counts.GetValueOrDefault(counter) + 1;
            return true;
        }
    }

    public bool Acknowledge(string key)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(key, out var s)) return false;
            s.Requests.Clear(); s.RequestTools.Clear(); s.Working = false; s.Tools.Clear(); s.Terminal = "unknown";
            s.LastNotice = "用户清除了提醒，执行状态尚未确认";
            return true;
        }
    }

    public Snapshot GetSnapshot()
    {
        lock (gate)
        {
            var timestamp = now();
            foreach (var s in sessions.Values)
            {
                // Waiting for a user is intentionally unbounded. Only a silent
                // working state expires, so a missing Stop cannot stay visible forever.
                if (s.Working && s.Requests.Count == 0 && timestamp - s.LastSeen > MaxWorkingMs)
                {
                    s.Working = false;
                    s.Tools.Clear();
                    s.Terminal = "unknown";
                    s.LastNotice = "工作状态超时，等待新的 Hook";
                }
            }
            var views = sessions.Values.OrderByDescending(s => s.Requests.Count > 0).ThenByDescending(s => s.LastSeen).Select(s => new SessionView(s.Key, s.Source, s.Project, s.SessionId, s.DisplayState, s.Tools.Count, s.Requests.Values.Count(v => v == "permission"), s.Requests.Values.Count(v => v == "input"), s.UpdatedAt, s.LastNotice)).ToList();
            int Priority(SessionView x) => x.State switch { "待授权" => 0, "待回答" => 1, "异常" => 2, "调用工具" => 3, "工作中" => 4, "状态未知" => 5, "任务完成" => 6, "本轮完成" => 7, _ => 99 };
            var visible = views.Where(x => IsActive(x.State)).OrderBy(Priority).ThenByDescending(x => x.UpdatedAt).FirstOrDefault();
            if (visible == null)
            {
                visible = views.Where(x => x.State is "任务完成" or "本轮完成")
                    .Where(x => sessions[x.Key].CompletedAt >= 0 && timestamp - sessions[x.Key].CompletedAt < CompletionToastMs)
                    .OrderByDescending(x => x.UpdatedAt)
                    .FirstOrDefault();
            }
            string text = visible == null ? "" : $"{DisplaySource(visible.Source)}:{visible.State}";
            string tip = visible == null ? "无活动 Agent" : string.Join("\n", views.Where(x => x.State != "空闲" && x.State != "会话结束").Take(12).Select(x => $"{DisplaySource(x.Source)} | {(x.Project.Length > 0 ? x.Project : x.SessionId)} | {x.State} | {x.UpdatedAt.ToLocalTime():HH:mm:ss}"));
            if (views.Count > 12) tip += $"\n另有 {views.Count - 12} 个会话；点击查看";
            return new(text, tip, views, accepted, rejected, new(counts));
        }
    }
}
