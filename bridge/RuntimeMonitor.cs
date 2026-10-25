using System.Text.Json;
namespace AgentStatus;

public sealed record RuntimeInfo(string Version, int Schema, string Mode, string Epoch);
public sealed record StreamBoundary(int Schema, string Epoch, long Revision);
public sealed record SseMessage(string Id, string Type, string Data);

public sealed class RuntimeSubscription(string epoch, StateStore state)
{
    bool ready;
    long revision;
    public void Receive(SseMessage message)
    {
        var parts = message.Id.Split(':');
        if (parts.Length != 2 || parts[0] != epoch || !long.TryParse(parts[1], out long id) || id < 0)
            throw new InvalidDataException("Invalid stream cursor");
        if (message.Type is "ready" or "gap")
        {
            var boundary = Wire.Read<StreamBoundary>(message.Data);
            if (boundary.Schema != 3 || boundary.Epoch != epoch || boundary.Revision != id || ready && message.Type == "ready")
                throw new InvalidDataException("Invalid stream boundary");
            state.Reset(message.Type == "ready" ? "Runtime 已连接" : "Runtime 正在重连", message.Type == "ready", id);
            if (message.Type == "gap") throw new IOException("Stream gap");
            ready = true; revision = id; return;
        }
        if (!ready) throw new InvalidDataException("Missing ready boundary");
        if (message.Type != "fact") throw new InvalidDataException("Unknown SSE type");
        var fact = Wire.Read<RuntimeFact>(message.Data);
        if (fact.Schema != 3 || fact.Id != id || fact.Historical) throw new InvalidDataException("Invalid live fact");
        if (id <= revision) return;
        state.Apply(fact with { SequenceGap = fact.SequenceGap || id != revision + 1 });
        revision = id;
    }
}

public sealed class RuntimeMonitor(StateStore state, RuntimeService? service = null, RuntimeClient? client = null)
{
    readonly object gate = new();
    CancellationTokenSource? attempt;
    bool reconnectRequested;
    bool connecting;
    public void Reconnect()
    {
        lock (gate)
        {
            if (connecting) return;
            reconnectRequested = true;
            state.Reset("正在发现Runtime服务");
            attempt?.Cancel();
        }
    }
    public async Task Run(CancellationToken ct)
    {
        using var ownedClient = client == null ? new RuntimeClient() : null;
        var http = client ?? ownedClient!;
        using var ownedService = service == null ? new RuntimeService() : null;
        var discovery = service ?? ownedService!;
        int attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (gate)
            {
                attempt = connectionLifetime;
                connecting = true;
                if (reconnectRequested) { discovery.RetryStart(); attempts = 0; reconnectRequested = false; }
                state.Reset("正在发现Runtime服务");
            }
            try
            {
                var connection = await discovery.Discover(connectionLifetime.Token);
                var subscription = new RuntimeSubscription(connection.Epoch, state);
                await http.SubscribeAsync(connection, message =>
                {
                    subscription.Receive(message);
                    if (message.Type == "ready") { attempts = 0; lock (gate) { connecting = false; } }
                    return Task.CompletedTask;
                }, () => { }, connectionLifetime.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { lock (gate) { attempt = null; connecting = false; } break; }
            catch (OperationCanceledException) when (connectionLifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is InvalidDataException or IOException or HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
            {
                // Only exception types are recorded; HTTP URLs, tokens and bodies never enter diagnostics.
                Diagnostics.Write("runtime", error.GetType().Name);
                state.Reset(error is RuntimeServiceException ? error.Message : "Runtime 未连接 · 正在重连");
            }
            lock (gate) { connecting = false; }
            try { await Task.Delay(new[] { 1000, 2000, 5000, 10000 }[Math.Min(attempts++, 3)], connectionLifetime.Token); }
            catch (OperationCanceledException) { }
            finally { lock (gate) { attempt = null; } }
        }
        lock (gate) { attempt = null; connecting = false; }
        state.Reset("显示桥接已停止");
    }
}
