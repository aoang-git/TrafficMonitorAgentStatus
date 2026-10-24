using AgentStatus;
using System.Text.Json;

static class ReportedStateTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var settings = new SettingsStore(Path.Combine(root, "reported-settings.json"));
        settings.Save(AgentStatusSettings.Defaults);
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
        foreach (var (wire, label) in new[] { ("working", "工作中"), ("tool_activity", "调用工具"), ("waiting_confirmation", "待确认"),
            ("completed", "任务完成"), ("interrupted", "任务中断"), ("failed", "任务中断"), ("unknown", "状态未知") })
        {
            var state = New(); state.Apply(Report(wire)); now += 15001;
            check(Text(state) == "custom-agent:" + label, "reported " + wire + " uses direct result and its own lease");
            check(state.GetSnapshot().Sessions[0].Origin == "external", "reported " + wire + " identifies its origin");
            now = 60999; check(Text(state) != "", "reported " + wire + " visible before lease boundary");
            now = 61000;
            var snapshot = state.GetSnapshot();
            check(snapshot.Text == "" && snapshot.Sessions.Count == 0 && !snapshot.Tooltip.Contains("custom-agent"), "reported " + wire + " clears all displays at lease boundary");
        }
        var s = New(); s.Apply(File()); s.Apply(Report("completed", agent: "codex"));
        check(Text(s) == "Codex:任务完成" && s.GetSnapshot().Sessions.Count == 2, "reported result outranks and stays separate from same file session");
        s.Apply(File("approval.observed", data: new() { RequestId = "approval", Phase = "requested" }));
        check(Text(s) == "Codex:任务完成", "file confirmation cannot override or change final reported state");
        now += 60000; check(Text(s) == "Codex:待确认", "expired report falls back to independently tracked file state");
        s = New(); s.Apply(File()); s.Apply(Report("working", agent: "codex"));
        settings.Save(settings.Current with { EnabledSources = new() { ["codex"] = false } });
        check(Text(s) == "Codex:工作中" && s.GetSnapshot().Sessions.Count == 1 && s.GetSnapshot().Sessions[0].Origin == "external", "tool filter never filters reported state");
        settings.Save(settings.Current with { ShowReportedStates = false }); check(Text(s) == "" && s.GetSnapshot().Sessions.Count == 0, "reported toggle hides reports independently");
        now += 20000; s.Apply(Report("waiting_confirmation", agent: "codex"));
        settings.Save(settings.Current with { ShowReportedStates = true }); check(Text(s) == "Codex:待确认", "hidden reports continue receiving updates");
        settings.Save(settings.Current with { ShowReportedStates = false }); now += 60000;
        settings.Save(settings.Current with { ShowReportedStates = true }); check(Text(s) == "", "reenabling never renews or revives expired reports");
        s = New(); var duplicate = Report("working"); s.Apply(duplicate); now += 59000;
        check(!s.Apply(duplicate), "duplicate report revision rejected"); now += 1000; check(Text(s) == "", "duplicate report does not renew lease");
        s = New(); s.Apply(Report("working")); now += 59000; s.Apply(Report("working")); now += 1000;
        check(Text(s) == "custom-agent:工作中", "new valid report renews lease even when state unchanged");
        now += 59000; check(Text(s) == "", "renewed lease also expires");
        s = New(); s.Apply(Report("completed")); s.Apply(Report("working"));
        check(Text(s) == "custom-agent:工作中", "same-operation report after completion is authoritative");
        s.Apply(Report("completed", operation: "new")); s.Apply(Report("tool_activity", operation: "old"));
        check(Text(s) == "custom-agent:调用工具", "reported result follows receive order without file operation inference");
        s.Apply(Report("idle", operation: null)); check(Text(s) == "" && s.GetSnapshot().Sessions.Count == 0, "reported idle removes only its current result");
        s = New(); s.Apply(Report("waiting_confirmation", source: "a")); s.Apply(Report("completed", source: "b"));
        check(Text(s) == "custom-agent:任务完成" && s.GetSnapshot().Sessions.Count == 2, "different producers stay independent and newest report wins over state priority");
        s.Apply(Report("idle", source: "b", operation: null)); check(Text(s) == "custom-agent:待确认", "idle falls back to another producer's live report");
        s.Apply(Report("working", agent: "other")); s.Apply(Report("working", session: "other")); s.Apply(Report("working", ns: "other"));
        check(s.GetSnapshot().Sessions.Count == 4, "agent namespace and session identities isolate reports");
        s = New(); s.Apply(Report("working")); now += 30000;
        settings.Save(settings.Current with { ReportedStateLeaseMs = 20000 }); check(Text(s) == "", "shorter lease takes effect from original receive time");
        settings.Save(settings.Current with { ReportedStateLeaseMs = 300000 }); check(Text(s) == "", "lease increase never revives an already removed report");
        s = New(); s.Apply(Report("working")); now += 30000;
        settings.Save(settings.Current with { ReportedStateLeaseMs = 120000 }); check(Text(s) != "", "lease update applies while report is still valid"); now = 61000; check(Text(s) != "", "longer lease extends a still-valid report from receive time");
        now = 121000; check(Text(s) == "", "extended report expires at recalculated deadline");
        s = New(); s.Apply(Report("working")); now += 60000;
        settings.Save(settings.Current with { ReportedStateLeaseMs = 300000 }); check(Text(s) == "", "extension cannot revive expiry even if no intervening snapshot occurred");
        s = New(); settings.Save(settings.Current with { ReportedStateLeaseMs = 1000, MaxWorkingMs = 30000, MaxWaitingMs = 60000 });
        s.Apply(Report("waiting_confirmation")); now += 1000; check(Text(s) == "", "reported waiting uses lease rather than confirmation timeout");
        s = New(); settings.Save(settings.Current with { ReportedStateLeaseMs = 120000, MaxWorkingMs = 30000 });
        s.Apply(Report("working")); now += 31000; check(Text(s) == "custom-agent:工作中", "reported work bypasses collected-work timeout");
        s = New(); s.Apply(Report("working")); now += 59000;
        s.Apply(File("source.health", agent: "custom-agent", data: new() { Health = "degraded" }));
        s.Apply(File(agent: "custom-agent")); now += 1000;
        check(s.GetSnapshot().Sessions.All(v => v.Origin == "file"), "file health and activity never modify or renew reported lease");
        s = New(); s.Apply(Report("working")); check(s.GetSnapshot().KnownSources.Length == 0, "external-only agent never creates a file tool checkbox");
        s.Apply(File(agent: "native-agent")); check(s.GetSnapshot().KnownSources.SequenceEqual(new[] { "native-agent" }), "only file agents enter observed tool definitions");
        var external = s.GetSnapshot().Sessions.Single(v => v.Origin == "external");
        check(s.Acknowledge(external.Key) && s.GetSnapshot().Sessions.All(v => v.Origin == "file") && Text(s) == "native-agent:工作中", "clearing report removes it without manufacturing unknown or changing collected state");
        s = New(); s.Apply(Report("failed")); settings.Save(settings.Current with { ShowSource = false, DisplayTexts = new() { ["taskInterrupted"] = "结束啦" } });
        check(Text(s) == "结束啦", "reported result respects source visibility and customized text");
        s.Reset("重连"); check(s.GetSnapshot().Sessions.Count == 0 && Text(s) == "", "reset clears report leases");
        s = New(); check(!s.Apply(Report("arbitrary")) && s.GetSnapshot().Sessions.Count == 0, "unsupported reported state does not become working");
        check(!s.Apply(Report("completed", operation: null)), "invalid required operation rejected");
        check(!s.Apply(Report("working") with { Historical = true }), "historical report rejected");
        settings.Save(settings.Current with { ReportedStateLeaseMs = 0 }); check(settings.Current.ReportedStateLeaseMs == 1000, "lease cannot be unlimited");
        settings.Save(settings.Current with { ReportedStateLeaseMs = int.MaxValue }); check(settings.Current.ReportedStateLeaseMs == 3600000, "lease has finite upper bound");
        // Existing version-2 settings gain defaults without overwriting existing preferences.
        var legacyPath = Path.Combine(root, "previous-settings.json");
        System.IO.File.WriteAllText(legacyPath, "{\"configVersion\":2,\"showSource\":false,\"displayTexts\":{\"working\":\"忙\"}}");
        var legacy = new SettingsStore(legacyPath).Current;
        check(legacy.ShowReportedStates && legacy.ReportedStateLeaseMs == 60000 && !legacy.ShowSource && legacy.DisplayTexts["working"] == "忙", "older settings gain lease defaults and preserve preferences");
        settings.Save(settings.Current with { ShowReportedStates = false, ReportedStateLeaseMs = 15000 });
        var reloaded = new SettingsStore(settings.Path).Current;
        check(!reloaded.ShowReportedStates && reloaded.ReportedStateLeaseMs == 15000, "reported settings persist across reload");
        settings.ResetToDefaults(); check(settings.Current.ShowReportedStates && settings.Current.ReportedStateLeaseMs == 60000, "reset restores reported display defaults");
    }
}

