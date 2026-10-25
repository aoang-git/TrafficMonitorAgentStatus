using AgentStatus;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Diagnostics;

static class RuntimeIntegration
{
    public static async Task Run(string root, Action<bool, string> check, string[] args)
    {
        await using var fake = new FakeRuntime(Path.Combine(root, "mock-runtime"));
        var settings = new SettingsStore(); settings.Save(AgentStatusSettings.Defaults);
        using var service = new RuntimeService(fake.Directory, Path.Combine(root, "not-installed.exe"));
        using var lifetime = new CancellationTokenSource();
        var server = new Server(service).Run(lifetime.Token);
        try
        {
            async Task<Snapshot> Snapshot() => (await Wire.Request(new { op = "snapshot" }, 1500)).Deserialize<Snapshot>(JsonWire.Options)!;
            async Task Await(Func<Snapshot, bool> predicate, string name, int timeout = 7000)
            {
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < timeout)
                { if (predicate(await Snapshot())) { check(true, name); return; } await Task.Delay(30); }
                throw new Exception("FAIL: " + name);
            }
            await Await(s => s.Connected, "real HTTP discovery info and SSE ready");
            await fake.Emit("working"); await Await(s => s.Text == "Codex:工作中", "HTTP SSE reaches bridge snapshot");
            if (args.Length == 2 && args[0] == "--integration")
            {
                await Task.Delay(250);
                var info = new ProcessStartInfo(Path.Combine(Path.GetFullPath(args[1]), "PluginProbe.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var arg in new[] { Path.Combine(Path.GetFullPath(args[1]), "AgentStatus.dll"), "20", "Codex:工作中" }) info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!; string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(); check(process.ExitCode == 0 && output.Contains("PASS"), "HTTP SSE to shared memory to native DLL ABI concurrency and unload");
            }
            var removed = await Wire.Request(new { op = "emit", events = new object[0] });
            check(!removed.GetProperty("ok").GetBoolean(), "legacy event IPC removed");
            await fake.Emit("waiting_confirmation"); await Await(s => s.Text == "Codex:待确认", "external confirmation display");
            await fake.Emit("completed"); await Await(s => s.Text == "Codex:任务完成", "external completion display");
            await fake.EmitFileInput();
            await Await(s => s.Text == "Codex:任务完成" && s.Sessions.Any(v => v.Origin == "file") && s.Sessions.Any(v => v.Origin == "external"), "reported and file SSE states remain separate with report priority");
            settings.Save(settings.Current with { EnabledSources = new() { ["codex"] = false } });
            await Await(s => s.Text == "Codex:任务完成" && s.Sessions.All(v => v.Origin == "external"), "file tool toggle cannot hide report through real SSE");
            settings.Save(settings.Current with { ShowReportedStates = false });
            await Await(s => s.Text == "" && s.Sessions.Count == 0, "reported checkbox hides result immediately");
            settings.Save(settings.Current with { ShowReportedStates = true, CompletionToastMs = 1000 });
            await Await(s => s.Text == "" && s.Sessions.Count == 0, "reported completion expiry removes real SSE result without unknown");
            settings.Save(settings.Current with { EnabledSources = new(), CompletionToastMs = 5000 });
            await Await(s => s.Text == "Codex:工作中" && s.Sessions.All(v => v.Origin == "file"), "expired report falls back to collected SSE session and does not revive after duration increase");
            fake.Drop(); await Await(s => !s.Connected && s.Sessions.Count == 0, "disconnect clears task state");
            await Await(s => s.Connected && s.Text == "", "reconnect never restores prior task");
            await fake.Emit("tool_activity", "second"); await Await(s => s.Text == "Codex:调用工具", "new activity after reconnect");
            await fake.Gap(); await Await(s => !s.Connected && s.Text == "", "SSE gap clears and disconnects");
            await Await(s => s.Connected && s.Sessions.Count == 0, "gap reconnect starts empty");
            // Rotate credentials without publishing discovery yet: the next read must see 401.
            fake.Token = "rotated-read-token"; fake.Drop();
            await fake.WaitFor(() => fake.Unauthorized > 0);
            fake.PublishDiscovery(); await Await(s => s.Connected, "401 triggers fresh discovery and credential reload", 15000);
            fake.Epoch = "replacement-epoch"; fake.Revision = 0; fake.PublishDiscovery(); fake.Drop();
            await fake.WaitFor(() => fake.LastSubscribedEpoch == "replacement-epoch");
            await Await(s => s.Connected && s.Text == "", "service epoch change clears state");
            fake.InfoEpochOverride = "mismatch"; int previous = fake.InfoRequests; fake.Drop();
            await fake.WaitFor(() => fake.InfoRequests > previous);
            await Await(s => !s.Connected, "discovery info epoch mismatch rejected");
            fake.InfoEpochOverride = null; await Await(s => s.Connected, "corrected instance rediscovers", 15000);
            await using var next = new FakeRuntime(Path.Combine(root, "next-runtime"));
            fake.Drop(); await Await(s => !s.Connected, "old instance disconnects before rediscovery");
            File.Copy(Path.Combine(next.Directory, "private", "connection.json"), Path.Combine(fake.Directory, "private", "connection.json"), true);
            var reconnect = await Wire.Request(new { op = "connect" });
            check(reconnect.GetProperty("ok").GetBoolean(), "discover-and-connect IPC accepted");
            await next.WaitFor(() => next.LastSubscribedEpoch != "");
            await next.Emit("working"); await Await(s => s.Text == "Codex:工作中", "manual discovery reconnects using newly published service connection");
            var accepted = (await Snapshot()).Accepted;
            await Wire.Request(new { op = "connect" });
            check((await Snapshot()).Text == "Codex:工作中" && (await Snapshot()).Accepted == accepted, "connect while healthy retains current display");
            check(fake.Routes.All(p => p is "/v3/info" or "/v3/events"), "client never reads recent history or writes ingest");
            var stop = await Wire.Request(new { op = "stop" }); check(stop.GetProperty("ok").GetBoolean(), "bridge shutdown command");
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { lifetime.Cancel(); await server; }
    }
}

sealed class FakeRuntime : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource lifetime = new();
    readonly ConcurrentBag<TcpClient> clients = [];
    readonly ConcurrentBag<Task> workers = [];
    readonly Task accept;
    Channel<string>? channel;
    public string Directory { get; }
    public string Epoch = "fixture-epoch", Token = "fixture-read-token", LastSubscribedEpoch = "";
    public string? InfoEpochOverride;
    public long Revision;
    public int Unauthorized, InfoRequests, Subscriptions;
    public int InfoSchema = 3;
    public ConcurrentBag<string> Routes = [];
    public FakeRuntime(string directory)
    {
        Directory = directory; System.IO.Directory.CreateDirectory(Path.Combine(directory, "private"));
        listener.Start(); PublishDiscovery(); accept = Accept();
    }
    public void PublishDiscovery() => File.WriteAllText(Path.Combine(Directory, "private", "connection.json"), JsonSerializer.Serialize(new
    { url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port, token = Token, pid = Environment.ProcessId, epoch = Epoch }));
    async Task Accept()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            { var client = await listener.AcceptTcpClientAsync(lifetime.Token); clients.Add(client); workers.Add(Handle(client)); }
        }
        catch (OperationCanceledException) { }
    }
    async Task Handle(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
                var line = await reader.ReadLineAsync(lifetime.Token); if (line == null) return;
                string route = line.Split(' ')[1]; Routes.Add(route); string authorization = "";
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(lifetime.Token)))
                    if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) authorization = line[14..].Trim();
                if (authorization != "Bearer " + Token)
                { Interlocked.Increment(ref Unauthorized); await Write(stream, "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"); return; }
                if (route == "/v3/info")
                {
                    Interlocked.Increment(ref InfoRequests);
                    string body = JsonSerializer.Serialize(new { version = "0.3.0", schema = InfoSchema, mode = "facts", epoch = InfoEpochOverride ?? Epoch });
                    await Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body); return;
                }
                if (route != "/v3/events") throw new Exception("Unexpected route");
                var ownChannel = Channel.CreateUnbounded<string>(); channel = ownChannel; LastSubscribedEpoch = Epoch;
                Interlocked.Increment(ref Subscriptions);
                await Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n" + Frame("ready", JsonSerializer.Serialize(new { schema = 3, epoch = Epoch, revision = Revision })));
                await foreach (var frame in ownChannel.Reader.ReadAllAsync(lifetime.Token)) await Write(stream, frame);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    static async Task Write(NetworkStream stream, string text)
    { await stream.WriteAsync(Encoding.UTF8.GetBytes(text)); await stream.FlushAsync(); }
    string Frame(string type, string data) => $"id: {Epoch}:{Revision}\nevent: {type}\ndata: {data}\n\n";
    public async Task Emit(string state, string operation = "first")
    {
        await WaitFor(() => channel != null); Revision++;
        var fact = new RuntimeFact { Schema = 3, Id = Revision, Agent = "codex", SessionId = "fixture", SessionNamespace = "test", OperationId = operation,
            SourceId = "mock-source", Kind = "state.reported", Origin = "external", Data = new() { State = state } };
        await channel!.Writer.WriteAsync(Frame("fact", JsonSerializer.Serialize(fact, JsonWire.Options)));
    }
    public async Task EmitFileInput()
    {
        await WaitFor(() => channel != null); Revision++;
        var fact = new RuntimeFact { Schema = 3, Id = Revision, Agent = "codex", SessionId = "fixture", SessionNamespace = "test", OperationId = "first",
            SourceId = "mock-source", Kind = "input.observed", Origin = "file", Data = new() };
        await channel!.Writer.WriteAsync(Frame("fact", JsonSerializer.Serialize(fact, JsonWire.Options)));
    }
    public async Task Gap() { Revision += 10; await channel!.Writer.WriteAsync(Frame("gap", JsonSerializer.Serialize(new { schema = 3, epoch = Epoch, revision = Revision }))); }
    public void Drop() { channel?.Writer.TryComplete(); channel = null; }
    public async Task WaitFor(Func<bool> predicate)
    { var timer = Stopwatch.StartNew(); while (!predicate()) { if (timer.ElapsedMilliseconds > 15000) throw new TimeoutException("Mock Runtime wait"); await Task.Delay(20); } }
    public async ValueTask DisposeAsync()
    { lifetime.Cancel(); listener.Stop(); foreach (var client in clients) client.Dispose(); await accept; await Task.WhenAll(workers); lifetime.Dispose(); }
}
