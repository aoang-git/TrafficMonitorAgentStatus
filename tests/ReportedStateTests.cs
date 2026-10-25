using AgentStatus;

static class ReportedStateTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var settings = new SettingsStore(Path.Combine(root, "reported-settings.json"));
        long now = 1000, revision = 0;
        StateStore New()
        {
            settings.ResetToDefaults(); now = 1000;
            var state = new StateStore(() => now, settings); state.Reset("Runtime 已连接", true); return state;
        }
        RuntimeFact Report(string state, string agent = "custom-agent", string source = "producer-a", string session = "same", string ns = "same", string? operation = "op") => new()
        {
            Schema = 3, Id = ++revision, Agent = agent, SourceId = source, SessionId = session, SessionNamespace = ns,
            OperationId = operation, Origin = "external", Kind = "state.reported", Data = new() { State = state }
        };
        RuntimeFact File(string kind = "input.observed", string agent = "codex", FactData? data = null) => new()
        {
            Schema = 3, Id = ++revision, Agent = agent, SourceId = "producer-a", SessionId = "same", SessionNamespace = "same",
            OperationId = "op", Origin = "file", Kind = kind, Data = data ?? new()
        };
        string Text(StateStore state) => state.GetSnapshot().Text;
        void Empty(StateStore state, string name)
        {
            var snapshot = state.GetSnapshot();
            check(snapshot.Text == "" && snapshot.Sessions.Count == 0 && !snapshot.Tooltip.Contains("custom-agent"), name);
        }
        foreach (var (wire, label, duration) in new[] { ("completed", "任务完成", 5000), ("interrupted", "任务中断", 15000), ("failed", "任务中断", 15000), ("unknown", "状态未知", 15000) })
        {
            var state = New(); state.Apply(Report(wire)); now += duration - 1;
            check(Text(state) == "custom-agent:" + label && state.GetSnapshot().Sessions[0].Origin == "external", wire + " uses shared toast duration");
            state.Apply(Report(wire)); now++;
            Empty(state, wire + " repeated result does not extend toast");
            state.Apply(Report(wire)); Empty(state, wire + " repeated result does not redisplay after expiry");
            state.Apply(Report(wire, operation: "next")); check(Text(state) != "", wire + " new operation can notify again");
        }
        foreach (var wire in new[] { "working", "tool_activity", "waiting_confirmation" })
        {
            var state = New(); int duration = wire == "waiting_confirmation" ? 1800000 : 300000;
            state.Apply(Report(wire)); now += duration - 1;
            check(Text(state) != "" && Text(state) != "custom-agent:状态未知", wire + " active before inactivity boundary");
            now++; check(Text(state) == "custom-agent:状态未知", wire + " times out to unknown");
            now += 14999; check(Text(state) == "custom-agent:状态未知", wire + " timeout uses notice duration");
            now++; Empty(state, wire + " timeout notice clears all displays");
            state.Apply(Report(wire)); check(Text(state) != "custom-agent:状态未知" && Text(state) != "", wire + " fresh report resumes activity");
        }
        var s = New(); s.Apply(Report("working")); now += 299999; s.Apply(Report("working")); now++;
        check(Text(s) == "custom-agent:工作中", "same active state with new revision refreshes progress");
        now += 299999; check(Text(s) == "custom-agent:状态未知", "refreshed work times out from new progress");
        s = New(); var duplicate = Report("working"); s.Apply(duplicate); now += 299999;
        check(!s.Apply(duplicate), "duplicate SSE revision rejected"); now++;
        check(Text(s) == "custom-agent:状态未知", "duplicate SSE revision never refreshes progress");
        s = New(); s.Apply(Report("working")); now += 315000;
        Empty(s, "late snapshot does not create a fresh timeout reminder");
        s = New(); s.Apply(Report("working")); now += 300000;
        settings.Save(settings.Current with { MaxWorkingMs = 600000 });
        check(Text(s) == "custom-agent:状态未知", "extension cannot undo an elapsed inactivity timeout");
        s = New(); s.Apply(Report("completed")); now += 5000;
        settings.Save(settings.Current with { CompletionToastMs = 60000 }); Empty(s, "extension cannot revive expired completion without intervening snapshot");
        s = New(); s.Apply(Report("completed")); now += 1000;
        settings.Save(settings.Current with { CompletionToastMs = 10000 });
        check(Text(s) == "custom-agent:任务完成", "live completion accepts a duration update before old expiry"); now += 4000;
        check(Text(s) == "custom-agent:任务完成", "still-live completion follows changed display setting");
        now += 5000; Empty(s, "extended completion expires at new deadline");
        s = New(); s.Apply(Report("working")); now += 30000;
        settings.Save(settings.Current with { MaxWorkingMs = 30000, NoticeToastMs = 1000 });
        check(Text(s) == "custom-agent:状态未知", "shorter work timeout takes effect immediately");
        now += 1000; Empty(s, "shortened timeout uses shared reminder setting");
        s = New(); settings.Save(settings.Current with { CompletionToastMs = 0 }); s.Apply(Report("completed")); Empty(s, "zero completion duration disables report toast");
        s = New(); s.Apply(File()); s.Apply(Report("completed", agent: "codex"));
        check(Text(s) == "Codex:任务完成" && s.GetSnapshot().Sessions.Count == 2, "reported result stays separate and outranks file session");
        s.Apply(File("approval.observed", data: new() { RequestId = "approval", Phase = "requested" }));
        check(Text(s) == "Codex:任务完成", "file confirmation does not alter report");
        now += 5000; check(Text(s) == "Codex:待确认", "completion expiry falls back to independent file confirmation");
        s = New(); s.Apply(File()); s.Apply(Report("working", agent: "codex"));
        settings.Save(settings.Current with { EnabledSources = new() { ["codex"] = false } });
        check(Text(s) == "Codex:工作中" && s.GetSnapshot().Sessions.Count == 1, "file filter never filters reports");
        settings.Save(settings.Current with { ShowReportedStates = false }); Empty(s, "report switch hides reports independently");
        now += 20000; s.Apply(Report("waiting_confirmation", agent: "codex"));
        settings.Save(settings.Current with { ShowReportedStates = true }); check(Text(s) == "Codex:待确认", "hidden reports still receive updates");
        settings.Save(settings.Current with { ShowReportedStates = false }); now += 1815000;
        settings.Save(settings.Current with { ShowReportedStates = true }); Empty(s, "reenabling never renews expired reports");
        s = New(); s.Apply(Report("completed")); s.Apply(Report("working"));
        check(Text(s) == "custom-agent:工作中", "same-operation activity after result remains authoritative");
        s.Apply(Report("completed", operation: "new")); s.Apply(Report("tool_activity", operation: "old"));
        check(Text(s) == "custom-agent:调用工具", "reports use receive order rather than file operation inference");
        s.Apply(Report("idle", operation: null)); Empty(s, "idle clears report");
        s = New(); s.Apply(Report("waiting_confirmation", source: "a")); s.Apply(Report("completed", source: "b"));
        check(Text(s) == "custom-agent:任务完成" && s.GetSnapshot().Sessions.Count == 2, "newest producer wins over state priority");
        s.Apply(Report("idle", source: "b", operation: null)); check(Text(s) == "custom-agent:待确认", "idle falls back to other live producer");
        s.Apply(Report("working", agent: "other")); s.Apply(Report("working", session: "other")); s.Apply(Report("working", ns: "other"));
        check(s.GetSnapshot().Sessions.Count == 4, "producer agent namespace and session isolate reports");
        s = New(); s.Apply(Report("working")); now += 299999;
        s.Apply(File("source.health", agent: "custom-agent", data: new() { Health = "degraded" })); s.Apply(File(agent: "custom-agent")); now++;
        check(s.GetSnapshot().Sessions.Single(v => v.Origin == "external").State == "状态未知", "file activity and health never renew report progress");
        s = New(); s.Apply(Report("working")); check(s.GetSnapshot().KnownSources.Length == 0, "external agent does not create file checkbox");
        s.Apply(File(agent: "native-agent")); check(s.GetSnapshot().KnownSources.SequenceEqual(new[] { "native-agent" }), "only file agents enter tool definitions");
        var external = s.GetSnapshot().Sessions.Single(v => v.Origin == "external");
        check(s.Acknowledge(external.Key) && s.GetSnapshot().Sessions.All(v => v.Origin == "file") && Text(s) == "native-agent:工作中", "clear hides report without changing file state");
        s = New(); s.Apply(Report("failed")); settings.Save(settings.Current with { ShowSource = false, DisplayTexts = new() { ["taskInterrupted"] = "结束啦" } });
        check(Text(s) == "结束啦", "report respects custom text and source visibility");
        s.Reset("重连"); Empty(s, "reset clears reports and repeat suppression");
        s.Reset("Runtime 已连接", true); s.Apply(Report("failed")); check(Text(s) == "结束啦", "new subscription accepts fresh result");
        s = New(); check(!s.Apply(Report("arbitrary")) && s.GetSnapshot().Sessions.Count == 0, "invalid reported state rejected");
        check(!s.Apply(Report("completed", operation: null)), "missing required operation rejected");
        check(!s.Apply(Report("working") with { Historical = true }), "historical report rejected");
        s = New(); for (int i = 0; i < 140; i++) { s.Apply(Report("completed", session: "bounded-" + i)); }
        check(s.GetSnapshot().Sessions.Count == 128, "report and suppression storage remains bounded");
        var legacyPath = Path.Combine(root, "previous-settings.json");
        System.IO.File.WriteAllText(legacyPath, """{"configVersion":2,"showSource":false,"runtimeDirectory":"obsolete","reportedStateLeaseMs":20000,"completionToastMs":7000,"noticeToastMs":17000,"maxWorkingMs":600000,"maxWaitingMs":2400000,"displayTexts":{"working":"忙"}}""");
        var legacyStore = new SettingsStore(legacyPath); var legacy = legacyStore.Current;
        check(legacy.ShowReportedStates && !legacy.ShowSource && legacy.DisplayTexts["working"] == "忙"
            && legacy.CompletionToastMs == 7000 && legacy.NoticeToastMs == 17000 && legacy.MaxWorkingMs == 600000 && legacy.MaxWaitingMs == 2400000, "legacy settings preserve four timings and display preferences");
        legacyStore.Save(legacy);
        var saved = System.IO.File.ReadAllText(legacyPath);
        check(!saved.Contains("reportedStateLeaseMs") && !saved.Contains("runtimeDirectory"), "saving removes obsolete lease and manual directory");
        settings.Save(settings.Current with { ShowReportedStates = false });
        check(!new SettingsStore(settings.Path).Current.ShowReportedStates, "report visibility persists");
    }
}
