using System.Text.Json;
namespace AgentStatus;

public static class JsonWire
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}
public sealed record FactData
{
    public string? Surface { get; init; }
    public string? Role { get; init; }
    public string? StopReason { get; init; }
    public string? Phase { get; init; }
    public string? Scope { get; init; }
    public string? Outcome { get; init; }
    public string? CallId { get; init; }
    public string? RequestId { get; init; }
    public string? State { get; init; }
    public bool? HasTools { get; init; }
    public string? Health { get; init; }
}
public sealed record RuntimeFact
{
    public int Schema { get; init; }
    public long Id { get; init; }
    public string Agent { get; init; } = "";
    public string SessionNamespace { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? OperationId { get; init; }
    public string SourceId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Origin { get; init; } = "";
    public bool Historical { get; init; }
    public bool SequenceGap { get; init; }
    public FactData Data { get; init; } = new();
}
public sealed record SessionView(string Key, string Source, string SessionId, string? OperationId,
    string State, int Tools, int Confirmations, DateTimeOffset UpdatedAt);
public sealed record Snapshot(string Text, string Tooltip, List<SessionView> Sessions,
    bool Connected, string RuntimeStatus, string[] KnownSources, long Accepted, long Rejected);

// Runtime broadcasts evidence. Only this consumer decides what the taskbar shows.
public sealed class StateStore
{
    sealed class Session
    {
        public required string Key, Agent, Id;
        public string? Operation;
        public string Source = "", State = "unknown";
        public readonly HashSet<string> Tools = [], Approvals = [], Retired = [];
        public readonly Queue<string> RetiredOrder = [];
        public long Progress, Updated, TerminalAt = -1, UnknownAt = -1;
        public long? Candidate;
        public DateTimeOffset UpdatedAt;
        public bool Incomplete, Terminal, ExplicitEnd;
    }
    readonly object gate = new();
    readonly Dictionary<string, Session> sessions = [];
    readonly HashSet<string> knownSources = new(StringComparer.OrdinalIgnoreCase);
    readonly SettingsStore settings;
    readonly Func<long> clock;
    readonly Func<DateTimeOffset> utcNow;
    long lastId, accepted, rejected;
    bool connected;
    string runtimeStatus = "正在连接 Runtime";
    const int MaxSessions = 128, MaxEntries = 1024;
    public StateStore(Func<long>? clock = null, SettingsStore? settings = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.clock = clock ?? (() => Environment.TickCount64);
        this.settings = settings ?? new SettingsStore();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }
    public void Reset(string status, bool isConnected = false, long revision = 0)
    {
        lock (gate) { sessions.Clear(); lastId = revision; connected = isConnected; runtimeStatus = status; }
    }
    static bool ValidId(string? text, bool required = true) => text != null && (!required || text.Length > 0)
        && text.Length <= 256 && !text.Any(char.IsControl);
    static string OperationKey(string? operation) => operation ?? "";
    static bool Active(string state) => state is "working" or "tool" or "waiting";
    static bool Alert(string state) => state is "unknown" or "error" or "interrupted";
    static void Finish(Session s, string state, long now, bool explicitEnd)
    {
        s.State = state; s.Terminal = true; s.ExplicitEnd = explicitEnd; s.TerminalAt = now;
        s.Tools.Clear(); s.Approvals.Clear(); s.Candidate = null;
        s.UnknownAt = state == "unknown" ? now : -1;
    }
    void Tick(Session s, long now, AgentStatusSettings config)
    {
        if (s.Terminal) return;
        if (s.Candidate is { } candidate && now - candidate >= 3000) Finish(s, "turnCompleted", candidate + 3000, false);
        else if (now - s.Progress >= (s.State == "waiting" ? config.MaxWaitingMs : config.MaxWorkingMs)) Finish(s, "unknown", now, false);
    }
    public bool Apply(RuntimeFact fact)
    {
        lock (gate)
        {
            if (!connected || fact.Schema != 3 || fact.Id <= lastId || fact.Historical || fact.Data is null
                || !ValidId(fact.Agent) || !ValidId(fact.SessionNamespace, false) || !ValidId(fact.SessionId, fact.Kind != "source.health")
                || !ValidId(fact.SourceId) || (fact.OperationId != null && !ValidId(fact.OperationId)))
            { rejected++; return false; }
            lastId = fact.Id;
            if (knownSources.Count < 256) knownSources.Add(fact.Agent);
            long now = clock();
            if (fact.Kind == "source.health")
            {
                foreach (var entry in sessions.Values.Where(s => s.Source == fact.SourceId && !s.Terminal))
                {
                    if (fact.Data.Health == "superseded") Finish(entry, "idle", now, true);
                    else if (fact.Data.Health is "degraded" or "unavailable")
                    { entry.State = "unknown"; if (entry.UnknownAt < 0) entry.UnknownAt = now; entry.Incomplete = true; entry.Candidate = null; }
                }
                accepted++; return true;
            }
            if (fact.Kind == "session.observed") { accepted++; return true; }
            if (fact.Kind is not ("input.observed" or "message.observed" or "tool.observed" or "approval.observed" or "lifecycle.observed" or "state.reported"))
            { rejected++; return false; }
            if (fact.Kind == "lifecycle.observed" && fact.Data.Scope is not ("agent_turn" or "agent_run")) return false;
            if (fact.Kind == "state.reported" && fact.Data.State is not ("working" or "tool_activity" or "waiting_confirmation" or "idle" or "completed" or "interrupted" or "failed" or "unknown")) return false;
            if (fact.Kind == "state.reported" && fact.Data.State is "working" or "tool_activity" or "waiting_confirmation" or "completed" or "interrupted" or "failed"
                && string.IsNullOrEmpty(fact.OperationId)) return false;
            var key = string.Join('\u001f', fact.Agent, fact.SessionNamespace, fact.SessionId);
            if (!sessions.TryGetValue(key, out var s))
            {
                if (sessions.Count >= MaxSessions) sessions.Remove(sessions.Values.MinBy(x => x.Updated)!.Key);
                s = new Session { Key = key, Agent = fact.Agent, Id = fact.SessionId, Operation = fact.OperationId, Progress = now };
                sessions.Add(key, s);
            }
            Tick(s, now, settings.Current);
            string? operation = fact.OperationId;
            if (operation == null && fact.Kind == "state.reported" && fact.Data.State is "idle" or "unknown") operation = s.Operation;
            if (s.Operation != operation)
            {
                if (s.Retired.Contains(OperationKey(operation))) { rejected++; return false; }
                s.Retired.Add(OperationKey(s.Operation)); s.RetiredOrder.Enqueue(OperationKey(s.Operation));
                if (s.RetiredOrder.Count > 128) s.Retired.Remove(s.RetiredOrder.Dequeue());
                s.Operation = operation; s.Tools.Clear(); s.Approvals.Clear(); s.Candidate = null;
                s.Incomplete = false; s.Terminal = false; s.ExplicitEnd = false; s.Progress = now; s.UnknownAt = -1;
            }
            bool explicitEnd = fact.Kind == "state.reported" && fact.Data.State is "completed" or "failed" or "interrupted"
                || fact.Kind == "lifecycle.observed" && fact.Data.Phase == "ended" && fact.Data.Outcome is "completed" or "failed" or "interrupted";
            if (s.Terminal && s.ExplicitEnd) { rejected++; return false; }
            bool unknown = fact.Kind == "state.reported" && fact.Data.State == "unknown"
                || fact.Kind == "lifecycle.observed" && fact.Data.Phase == "ended" && !explicitEnd;
            if (s.Terminal && !unknown) { s.Terminal = false; s.TerminalAt = -1; }
            s.Source = fact.SourceId; s.Updated = now; s.UpdatedAt = utcNow();
            s.Incomplete |= fact.SequenceGap; s.Candidate = null;
            if (!unknown) s.Progress = now;
            if (explicitEnd)
            {
                string outcome = fact.Kind == "state.reported" ? fact.Data.State! : fact.Data.Outcome!;
                Finish(s, outcome switch { "failed" => "error", "interrupted" => "interrupted", _ => fact.Data.Scope == "agent_run" ? "taskCompleted" : "turnCompleted" }, now, true);
            }
            else if (unknown)
            {
                s.State = "unknown"; if (s.UnknownAt < 0) s.UnknownAt = now;
            }
            else
            {
                s.UnknownAt = -1;
                string next = "working";
                switch (fact.Kind)
                {
                    case "tool.observed":
                        if (ValidId(fact.Data.CallId))
                        {
                            if (fact.Data.Phase == "finished") s.Tools.Remove(fact.Data.CallId!);
                            else if (s.Tools.Count < MaxEntries) s.Tools.Add(fact.Data.CallId!);
                            else s.Incomplete = true;
                        }
                        break;
                    case "approval.observed":
                        if (ValidId(fact.Data.RequestId))
                        {
                            if (fact.Data.Phase == "resolved") s.Approvals.Remove(fact.Data.RequestId!);
                            else if (s.Approvals.Count < MaxEntries) s.Approvals.Add(fact.Data.RequestId!);
                            else s.Incomplete = true;
                        }
                        break;
                    case "message.observed":
                        if (fact.Data.Outcome is "failed" or "interrupted" || fact.Data.StopReason is "max_tokens" or "length" or "error" or "aborted")
                        { next = "unknown"; s.UnknownAt = now; }
                        else if (fact.Origin == "file" && !s.Incomplete && fact.Data.Role == "assistant" && fact.Data.HasTools != true
                            && s.Tools.Count == 0 && s.Approvals.Count == 0
                            && (fact.Agent == "claude" && fact.Data.StopReason is "end_turn" or "stop_sequence" || fact.Agent == "pi" && fact.Data.StopReason == "stop")) s.Candidate = now;
                        break;
                    case "state.reported":
                        s.Tools.Clear(); s.Approvals.Clear();
                        next = fact.Data.State switch { "tool_activity" => "tool", "waiting_confirmation" => "waiting", "idle" => "idle", _ => "working" };
                        if (next == "idle") Finish(s, "idle", now, false);
                        break;
                }
                s.State = next == "unknown" ? next : s.Approvals.Count > 0 ? "waiting" : s.Tools.Count > 0 ? "tool" : next;
            }
            accepted++; return true;
        }
    }
    public bool Acknowledge(string key)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(key, out var s)) return false;
            Finish(s, "unknown", clock(), false); return true;
        }
    }
    public static string DisplaySource(string source) => source switch { "codex" => "Codex", "claude" => "Claude", "deepseek" => "DeepSeek", "pi" => "Pi", _ => source };
    public Snapshot GetSnapshot()
    {
        lock (gate)
        {
            var now = clock(); var config = settings.Current;
            foreach (var s in sessions.Values) Tick(s, now, config);
            var shown = sessions.Values.Where(s => config.EnabledSources.GetValueOrDefault(s.Agent, true)).ToArray();
            bool Visible(Session s) => Active(s.State) || s.State != "idle" && now - (s.Terminal ? s.TerminalAt : s.UnknownAt) < (Alert(s.State) ? config.NoticeToastMs : config.CompletionToastMs);
            var selected = shown.Where(Visible).OrderBy(s => s.State switch { "waiting" => 0, "tool" => 1, "working" => 2, "error" => 3, "unknown" => 4, "interrupted" => 5, "taskCompleted" => 6, _ => 7 }).ThenByDescending(s => s.Updated).FirstOrDefault();
            string Label(Session s) => config.DisplayTexts.GetValueOrDefault(s.State, s.State);
            string text = selected == null ? "" : (config.ShowSource ? DisplaySource(selected.Agent) + ":" : "") + Label(selected);
            string tooltip = !connected ? runtimeStatus : selected == null ? "无活动 Agent" : string.Join('\n', shown.Where(Visible).OrderByDescending(s => s.Updated).Take(12).Select(s => $"{DisplaySource(s.Agent)} | {s.Id} | {Label(s)}"));
            var views = shown.OrderByDescending(s => s.Updated).Select(s => new SessionView(s.Key, DisplaySource(s.Agent), s.Id, s.Operation, Label(s), s.Tools.Count, s.Approvals.Count, s.UpdatedAt.ToLocalTime())).ToList();
            return new(text, tooltip, views, connected, runtimeStatus, knownSources.Order().ToArray(), accepted, rejected);
        }
    }
}
