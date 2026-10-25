using AgentStatus;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Throws(Action action, string name) { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or JsonException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
string root = Path.Combine(AppContext.BaseDirectory, "tmas-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("TMAS_INSTANCE", "test" + Guid.NewGuid().ToString("N")[..12]);
Environment.SetEnvironmentVariable("TMAS_DATA_DIR", Path.Combine(root, "bridge"));
var settings = new SettingsStore();
settings.Save(AgentStatusSettings.Defaults);
long clock = 1000, id = 0;
StateStore NewState() { clock = 1000; var result = new StateStore(() => clock, settings); result.Reset("Runtime 已连接", true); return result; }
RuntimeFact F(string kind = "input.observed", string agent = "codex", string session = "A", string? operation = "op1", FactData? data = null) => new()
{ Schema = 3, Id = ++id, Agent = agent, SessionId = session, SessionNamespace = "local", OperationId = operation, SourceId = "src-" + session, Kind = kind, Origin = "file", Data = data ?? new() };
string Text(StateStore state) => state.GetSnapshot().Text;
try
{
    var s = NewState();
    s.Apply(F()); Check(Text(s) == "Codex:工作中", "new input displays work");
    Check(s.GetSnapshot().Sessions[0].UpdatedAt.Offset == DateTimeOffset.Now.Offset, "detail timestamps use the local timezone");
    var tool = F("tool.observed", data: new() { CallId = "a", Phase = "requested" }); s.Apply(tool);
    s.Apply(F("tool.observed", data: new() { CallId = "b", Phase = "requested" }));
    Check(s.GetSnapshot().Sessions[0].Tools == 2, "parallel tools tracked");
    Check(!s.Apply(tool), "duplicate revision rejected");
    s.Apply(F("tool.observed", data: new() { CallId = "a", Phase = "finished" })); Check(Text(s) == "Codex:调用工具", "one tool finishing preserves another");
    s.Apply(F("approval.observed", data: new() { RequestId = "r", Phase = "requested" }));
    clock++; s.Apply(F(session: "B")); Check(Text(s) == "Codex:待确认", "confirmation outranks newer work");
    s.Apply(F("approval.observed", data: new() { RequestId = "r", Phase = "resolved" })); Check(Text(s) == "Codex:调用工具", "confirmation resolution preserves tool");
    s.Apply(F("tool.observed", data: new() { CallId = "b", Phase = "finished" })); Check(Text(s) == "Codex:工作中", "last tool returns to working");
    clock++; s.Apply(F(agent: "pi", session: "C")); Check(Text(s) == "Pi:工作中", "newest equal priority wins");
    s.Apply(F("lifecycle.observed", agent: "pi", session: "C", data: new() { Scope = "agent_turn", Phase = "ended", Outcome = "failed" })); Check(Text(s) == "Codex:工作中", "failure does not displace activity");
    s = NewState(); s.Apply(F(operation: "first")); s.Apply(F(operation: "second"));
    Check(!s.Apply(F("lifecycle.observed", operation: "first", data: new() { Scope = "agent_turn", Phase = "ended", Outcome = "completed" })) && Text(s) == "Codex:工作中", "old operation cannot finish current work");
    s.Apply(F("lifecycle.observed", operation: "second", data: new() { Scope = "agent_turn", Phase = "ended", Outcome = "completed" }));
    Check(Text(s) == "Codex:任务完成", "explicit turn completion");
    Check(!s.Apply(F(operation: "second")), "late activity cannot revive explicit completion");
    clock += 5001; Check(Text(s) == "", "completion expires");
    s = NewState(); s.Apply(F("lifecycle.observed", data: new() { Scope = "agent_run", Phase = "ended", Outcome = "completed" })); Check(Text(s) == "Codex:任务完成", "run completion");
    s = NewState(); s.Apply(F("lifecycle.observed", data: new() { Scope = "agent_turn", Phase = "ended", Outcome = "interrupted" })); Check(Text(s) == "Codex:任务中断", "interruption result");
    s = NewState(); s.Apply(F("state.reported", data: new() { State = "failed" }) with { Origin = "external" }); Check(Text(s) == "Codex:任务中断", "external failure");
    s = NewState(); s.Apply(F("lifecycle.observed", data: new() { Scope = "agent_turn", Phase = "ended", Outcome = "failed" }));
    Check(Text(s) == "Codex:任务中断", "native failure shares interruption result");
    clock += 5001; Check(Text(s) == "Codex:任务中断", "interruption uses notice duration");
    clock += 10000; Check(Text(s) == "", "interruption notice expires");
    s = NewState(); s.Apply(F("state.reported", data: new() { State = "interrupted" }) with { Origin = "external" });
    Check(Text(s) == "Codex:任务中断", "external interruption shares failure result");
    settings.Save(settings.Current with { DisplayTexts = new() { ["taskCompleted"] = "任务完成", ["turnCompleted"] = "本轮完成", ["interrupted"] = "已中断", ["error"] = "异常" } });
    Check(settings.Current.DisplayTexts.Count == 6 && settings.Current.DisplayTexts["taskInterrupted"] == "任务中断"
        && !settings.Current.DisplayTexts.ContainsKey("turnCompleted"), "settings expose only the six merged display states");
    settings.ResetToDefaults();
    s = NewState(); s.Apply(F(agent: "claude"));
    s.Apply(F("message.observed", agent: "claude", data: new() { Role = "assistant", StopReason = "end_turn" }));
    clock += 2999; Check(Text(s) == "Claude:工作中", "settle delay does not finish early");
    clock++; Check(Text(s) == "Claude:任务完成", "normal quiet end shows result without explanation");
    Check(!s.GetSnapshot().Tooltip.Contains("推断"), "tooltip contains final result only");
    s.Apply(F("tool.observed", agent: "claude", data: new() { CallId = "later", Phase = "requested" })); Check(Text(s) == "Claude:调用工具", "new evidence after quiet end resumes activity");
    s = NewState(); s.Apply(F("tool.observed", agent: "pi", data: new() { CallId = "t", Phase = "requested" }));
    s.Apply(F("message.observed", agent: "pi", data: new() { Role = "assistant", StopReason = "stop" })); clock += 3001;
    Check(Text(s) == "Pi:调用工具", "pending tools block quiet end");
    s = NewState(); s.Apply(F(agent: "claude") with { SequenceGap = true });
    s.Apply(F("message.observed", agent: "claude", data: new() { Role = "assistant", StopReason = "end_turn" })); clock += 3001;
    Check(Text(s) == "Claude:工作中", "sequence gap prevents quiet success");
    s = NewState(); s.Apply(F()); clock += 299000;
    s.Apply(F("lifecycle.observed", data: new() { Scope = "agent_turn", Phase = "ended" })); clock += 1001;
    Check(Text(s) == "Codex:状态未知", "unknown does not renew progress"); clock += 15001; Check(Text(s) == "", "unknown notice expires");
    s = NewState(); s.Apply(F("approval.observed", data: new() { RequestId = "r", Phase = "requested" })); clock += 300001;
    Check(Text(s) == "Codex:待确认", "approval has longer timeout"); clock += 1500000;
    Check(Text(s) == "Codex:状态未知", "approval eventually expires");
    s = NewState(); s.Apply(F()); clock += 299000; s.Apply(F("source.health", data: new() { Health = "degraded" }));
    Check(Text(s) == "Codex:状态未知", "degraded source clears active certainty"); clock += 1001; s.GetSnapshot(); clock += 15001; Check(Text(s) == "", "health does not renew activity");
    s = NewState(); s.Apply(F()); var key = s.GetSnapshot().Sessions[0].Key;
    Check(s.Acknowledge(key) && Text(s) == "Codex:状态未知", "local clear does not report completion");
    settings.Save(settings.Current with { EnabledSources = new() { ["codex"] = false } }); Check(Text(s) == "", "source filtering");
    settings.Save(AgentStatusSettings.Defaults with { ShowSource = false, DisplayTexts = new() { ["working"] = "忙碌" } });
    s = NewState(); s.Apply(F()); Check(Text(s) == "忙碌", "custom text hot loads"); settings.ResetToDefaults();
    s.Reset("Runtime 未连接"); Check(Text(s) == "" && s.GetSnapshot().Sessions.Count == 0 && s.GetSnapshot().Tooltip == "Runtime 未连接", "disconnect clears all live state");
    Check(!s.Apply(F()), "disconnected facts rejected");
    s = NewState(); Check(!s.Apply(F() with { Historical = true }), "historical facts rejected");
    var stream = new RuntimeSubscription("epoch", s);
    Throws(() => stream.Receive(new("epoch:1", "fact", JsonSerializer.Serialize(F(), JsonWire.Options))), "fact before ready rejected");
    stream.Receive(new("epoch:10", "ready", "{\"schema\":3,\"epoch\":\"epoch\",\"revision\":10}"));
    var live = F() with { Id = 11 }; stream.Receive(new("epoch:11", "fact", JsonSerializer.Serialize(live, JsonWire.Options)));
    Check(Text(s) == "Codex:工作中", "ready establishes new subscription boundary");
    stream.Receive(new("epoch:11", "fact", JsonSerializer.Serialize(live, JsonWire.Options))); Check(s.GetSnapshot().Sessions.Count == 1, "duplicate SSE fact ignored");
    Throws(() => stream.Receive(new("other:12", "fact", "{}")), "epoch mismatch rejected");
    Throws(() => stream.Receive(new("epoch:20", "gap", "{\"schema\":3,\"epoch\":\"epoch\",\"revision\":20}")), "gap requests rediscovery");
    Check(Text(s) == "" && !s.GetSnapshot().Connected, "gap clears activity immediately");
    var parsed = new List<SseMessage>();
    await using (var bytes = new MemoryStream(Encoding.UTF8.GetBytes(": heartbeat\r\nevent: ready\r\nid: e:0\r\ndata: {\"schema\":3,\r\ndata: \"revision\":0}\r\n\r\n")))
        await foreach (var message in SseReader.ReadAsync(bytes, () => { }, default)) parsed.Add(message);
    Check(parsed.Count == 1 && parsed[0].Data.Contains('\n'), "SSE comments CRLF and multiline frames");
    await using (var stall = new StallStream())
    {
        bool timedOut = false;
        try { await foreach (var unused in SseReader.ReadAsync(stall, () => { }, default, TimeSpan.FromMilliseconds(30))) { } }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut, "SSE heartbeat timeout enforced");
    }
    string discovery = Path.Combine(root, "runtime"); Directory.CreateDirectory(Path.Combine(discovery, "private"));
    string discoveryFile = Path.Combine(discovery, "private", "connection.json");
    await File.WriteAllTextAsync(discoveryFile, "{}");
    bool badDiscovery = false; try { await RuntimeConnection.LoadAsync(discovery, default); } catch (InvalidDataException) { badDiscovery = true; }
    Check(badDiscovery, "malformed discovery rejected");
    await File.WriteAllTextAsync(discoveryFile, "{\"url\":\"http://example.com\",\"token\":\"secret\",\"pid\":1,\"epoch\":\"e\"}");
    badDiscovery = false; try { await RuntimeConnection.LoadAsync(discovery, default); } catch (InvalidDataException) { badDiscovery = true; }
    Check(badDiscovery, "nonloopback discovery rejected");
    ReportedStateTests.Run(root, Check);
    await RuntimeServiceTests.Run(root, Check);
    IpcSecurityTests.Run(Check);
    SettingsLayoutTests.Run(Check);
    await RuntimeIntegration.Run(root, Check, args);
    Console.WriteLine($"PASS: {passed} checks");
}
finally
{
    Environment.SetEnvironmentVariable("TMAS_DATA_DIR", null); Environment.SetEnvironmentVariable("TMAS_INSTANCE", null);
    // Only this test's randomly named, resolved temporary directory is removed.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}

sealed class StallStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
