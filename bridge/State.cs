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
    string State, int Tools, int Confirmations, DateTimeOffset UpdatedAt, string Origin);
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
    sealed record ReportedSession(string Key, string Agent, string Id, string? Operation, string State,
        long Received, long Revision, DateTimeOffset UpdatedAt, long Deadline);
    readonly Dictionary<string, ReportedSession> reported = [];
    int appliedReportedLeaseMs;
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
        appliedReportedLeaseMs = this.settings.Current.ReportedStateLeaseMs;
    }
    public void Reset(string status, bool isConnected = false, long revision = 0)
    {
        lock (gate) { sessions.Clear(); reported.Clear(); lastId = revision; connected = isConnected; runtimeStatus = status; }
    }
    static bool ValidId(string? text, bool required = true) => text != null && (!required || text.Length > 0)
        && text.Length <= 256 && !text.Any(char.IsControl);
    static string OperationKey(string? operation) => operation ?? "";
    static bool Active(string state) => state is "working" or "tool" or "waiting";
    static bool Alert(string state) => state is "unknown" or "taskInterrupted";
    static void Finish(Session s, string state, long now, bool explicitEnd)
    {
        s.State = state; s.Terminal = true; s.ExplicitEnd = explicitEnd; s.TerminalAt = now;
        s.Tools.Clear(); s.Approvals.Clear(); s.Candidate = null;
        s.UnknownAt = state == "unknown" ? now : -1;
    }
    void Tick(Session s, long now, AgentStatusSettings config)
    {
        if (s.Terminal) return;
        if (s.Candidate is { } candidate && now - candidate >= 3000) Finish(s, "taskCompleted", candidate + 3000, false);
        else if (now - s.Progress >= (s.State == "waiting" ? config.MaxWaitingMs : config.MaxWorkingMs)) Finish(s, "unknown", now, false);
    }
    void ExpireReported(long now, AgentStatusSettings config)
    {
        // Expire against the previous deadline before extending a changed lease.
        // A later settings edit must never resurrect a report whose lease already elapsed.
        foreach (var entry in reported.Values.Where(r => now >= r.Deadline).ToArray()) reported.Remove(entry.Key);
        if (appliedReportedLeaseMs == config.ReportedStateLeaseMs) return;
        foreach (var entry in reported.Values.ToArray())
        {
            long deadline = entry.Received + config.ReportedStateLeaseMs;
            if (now >= deadline) reported.Remove(entry.Key);
            else reported[entry.Key] = entry with { Deadline = deadline };
        }
        appliedReportedLeaseMs = config.ReportedStateLeaseMs;
    }
    bool ApplyReported(RuntimeFact fact, long now, AgentStatusSettings config)
    {
        string? state = fact.Data.State switch
        {
            "working" => "working", "tool_activity" => "tool", "waiting_confirmation" => "waiting",
            "completed" => "taskCompleted", "interrupted" or "failed" => "taskInterrupted",
            "unknown" => "unknown", "idle" => "idle", _ => null
        };
        if (state == null || (state is not ("idle" or "unknown") && string.IsNullOrEmpty(fact.OperationId)))
        { rejected++; return false; }
        var key = string.Join('\u001f', "external", fact.SourceId, fact.Agent, fact.SessionNamespace, fact.SessionId);
        if (state == "idle") reported.Remove(key);
        else
        {
            if (!reported.ContainsKey(key) && reported.Count >= MaxSessions)
                reported.Remove(reported.Values.MinBy(r => r.Revision)!.Key);
            reported[key] = new(key, fact.Agent, fact.SessionId, fact.OperationId, state, now, fact.Id,
                utcNow(), now + config.ReportedStateLeaseMs);
        }
        accepted++; return true;
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
            long now = clock();
            var config = settings.Current;
            ExpireReported(now, config);
            if (fact.Origin == "external" && fact.Kind == "state.reported") return ApplyReported(fact, now, config);
            if (fact.Origin != "file" || fact.Kind == "state.reported") { rejected++; return false; }
            if (knownSources.Count < 256) knownSources.Add(fact.Agent);
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
            if (fact.Kind is not ("input.observed" or "message.observed" or "tool.observed" or "approval.observed" or "lifecycle.observed"))
            { rejected++; return false; }
            if (fact.Kind == "lifecycle.observed" && fact.Data.Scope is not ("agent_turn" or "agent_run")) return false;
            var key = string.Join('\u001f', fact.Agent, fact.SessionNamespace, fact.SessionId);
            if (!sessions.TryGetValue(key, out var s))
            {
                if (sessions.Count >= MaxSessions) sessions.Remove(sessions.Values.MinBy(x => x.Updated)!.Key);
                s = new Session { Key = key, Agent = fact.Agent, Id = fact.SessionId, Operation = fact.OperationId, Progress = now };
                sessions.Add(key, s);
            }
            Tick(s, now, config);
            string? operation = fact.OperationId;
            if (s.Operation != operation)
            {
                if (s.Retired.Contains(OperationKey(operation))) { rejected++; return false; }
                s.Retired.Add(OperationKey(s.Operation)); s.RetiredOrder.Enqueue(OperationKey(s.Operation));
                if (s.RetiredOrder.Count > 128) s.Retired.Remove(s.RetiredOrder.Dequeue());
                s.Operation = operation; s.Tools.Clear(); s.Approvals.Clear(); s.Candidate = null;
                s.Incomplete = false; s.Terminal = false; s.ExplicitEnd = false; s.Progress = now; s.UnknownAt = -1;
            }
            bool explicitEnd = fact.Kind == "lifecycle.observed" && fact.Data.Phase == "ended" && fact.Data.Outcome is "completed" or "failed" or "interrupted";
            if (s.Terminal && s.ExplicitEnd) { rejected++; return false; }
            bool unknown = fact.Kind == "lifecycle.observed" && fact.Data.Phase == "ended" && !explicitEnd;
            if (s.Terminal && !unknown) { s.Terminal = false; s.TerminalAt = -1; }
            s.Source = fact.SourceId; s.Updated = now; s.UpdatedAt = utcNow();
            s.Incomplete |= fact.SequenceGap; s.Candidate = null;
            if (!unknown) s.Progress = now;
            if (explicitEnd)
            {
                string outcome = fact.Data.Outcome!;
                Finish(s, outcome is "failed" or "interrupted" ? "taskInterrupted" : "taskCompleted", now, true);
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
            ExpireReported(clock(), settings.Current);
            if (reported.Remove(key)) return true;
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
            ExpireReported(now, config);
            foreach (var s in sessions.Values) Tick(s, now, config);
            var shown = sessions.Values.Where(s => config.EnabledSources.GetValueOrDefault(s.Agent, true)).ToArray();
            var reports = config.ShowReportedStates ? reported.Values.OrderByDescending(r => r.Revision).ToArray() : [];
            bool Visible(Session s) => Active(s.State) || s.State != "idle" && now - (s.Terminal ? s.TerminalAt : s.UnknownAt) < (Alert(s.State) ? config.NoticeToastMs : config.CompletionToastMs);
            var selected = shown.Where(Visible).OrderBy(s => s.State switch { "waiting" => 0, "tool" => 1, "working" => 2, "taskInterrupted" => 3, "unknown" => 4, "taskCompleted" => 5, _ => 6 }).ThenByDescending(s => s.Updated).FirstOrDefault();
            string Label(string state) => config.DisplayTexts.GetValueOrDefault(state, state);
            var latestReport = reports.FirstOrDefault();
            string? agent = latestReport?.Agent ?? selected?.Agent;
            string? state = latestReport?.State ?? selected?.State;
            string text = state == null ? "" : (config.ShowSource ? DisplaySource(agent!) + ":" : "") + Label(state);
            var tips = reports.Select(r => $"{DisplaySource(r.Agent)} | {r.Id} | {Label(r.State)}")
                .Concat(shown.Where(Visible).OrderByDescending(s => s.Updated).Select(s => $"{DisplaySource(s.Agent)} | {s.Id} | {Label(s.State)}"));
            string tooltip = !connected ? runtimeStatus : state == null ? "无活动 Agent" : string.Join('\n', tips.Take(12));
            var views = reports.Select(r => new SessionView(r.Key, DisplaySource(r.Agent), r.Id, r.Operation, Label(r.State), 0, 0, r.UpdatedAt.ToLocalTime(), "external"))
                .Concat(shown.OrderByDescending(s => s.Updated).Select(s => new SessionView(s.Key, DisplaySource(s.Agent), s.Id, s.Operation, Label(s.State), s.Tools.Count, s.Approvals.Count, s.UpdatedAt.ToLocalTime(), "file"))).ToList();
            return new(text, tooltip, views, connected, runtimeStatus, knownSources.Order().ToArray(), accepted, rejected);
        }
    }
}
