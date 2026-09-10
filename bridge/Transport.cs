using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AgentStatus;

public static class Names
{
    public static string Suffix => WindowsIdentity.GetCurrent().User!.Value + "-" + Process.GetCurrentProcess().SessionId;
    public static string Pipe => "TMAS-v1-" + Suffix;
    public static string Map => "Local\\TMAS-v1-" + Suffix;
    public static string Lock => "Local\\TMAS-lock-v1-" + Suffix;
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrafficMonitorAgentStatus");
}

public sealed class SnapshotMap : IDisposable
{
    const int Size = 32768;
    readonly MemoryMappedFile map = MemoryMappedFile.CreateNew(Names.Map, Size);
    readonly MemoryMappedViewAccessor view;
    int sequence;
    public SnapshotMap() { view = map.CreateViewAccessor(); }
    public void Publish(Snapshot snapshot)
    {
        // Seqlock: an even, identical sequence on either side is a complete snapshot.
        view.Write(0, ++sequence);
        Thread.MemoryBarrier();
        view.Write(4, 0x53414d54);
        view.Write(8, (ulong)Environment.TickCount64);
        var text = Encoding.Unicode.GetBytes(snapshot.Text[..Math.Min(127, snapshot.Text.Length)] + '\0');
        var tip = Encoding.Unicode.GetBytes(snapshot.Tooltip[..Math.Min(4095, snapshot.Tooltip.Length)] + '\0');
        view.WriteArray(16, text, 0, text.Length);
        view.WriteArray(272, tip, 0, tip.Length);
        Thread.MemoryBarrier();
        view.Write(0, ++sequence);
    }
    public void Dispose() { view.Dispose(); map.Dispose(); }
}

public static class Wire
{
    public const int MaxBytes = 65536;
    public static async Task<byte[]> Read(Stream stream, CancellationToken ct, int max = MaxBytes)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > max) throw new InvalidDataException("Message size exceeds limit");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }
    public static async Task Write(Stream stream, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonWire.Options);
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Reply exceeds limit");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<JsonElement> Request(object value, int timeout = 500)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var client = new NamedPipeClientStream(".", Names.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);
        await Write(client, value, cts.Token);
        return JsonSerializer.Deserialize<JsonElement>(await Read(client, cts.Token, 1024 * 1024));
    }
}

public sealed class Server
{
    readonly SettingsStore settings = new();
    readonly StateStore state;

    public Server() { state = new StateStore(settings: settings); }

    public async Task Run(CancellationToken ct)
    {
        using var snapshot = new SnapshotMap();
        var codexDesktop = new CodexDesktopWatcher(state, settings);
        var codexDesktopTask = codexDesktop.Run(ct);
        // A fixed pool bounds concurrent connections and slow clients.
        var workers = Enumerable.Range(0, 8).Select(_ => Listen(ct)).ToArray();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            do { snapshot.Publish(state.GetSnapshot()); } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) { }
            try { await codexDesktopTask; }
            catch (OperationCanceledException) { }
        }
    }
    async Task Listen(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Names.Pipe, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(1000);
                var root = JsonSerializer.Deserialize<JsonElement>(await Wire.Read(pipe, deadline.Token));
                string op = root.GetProperty("op").GetString() ?? "";
                object result;
                switch (op)
                {
                    case "ping": result = new { ok = true, version = "0.1.0", pid = Environment.ProcessId }; break;
                    case "snapshot": result = state.GetSnapshot(); break;
                    case "ack": result = new { ok = state.Acknowledge(root.GetProperty("key").GetString() ?? "") }; break;
                    case "emit":
                        var events = root.GetProperty("events").Deserialize<List<StatusEvent>>(JsonWire.Options) ?? [];
                        if (events.Count > 32) throw new InvalidDataException("Too many events");
                        bool ok = true;
                        foreach (var e in events) ok &= state.Apply(e);
                        result = new { ok }; break;
                    case "hook":
                        string source = root.GetProperty("source").GetString() ?? "";
                        string? eventOverride = root.TryGetProperty("event_override", out var ev) ? ev.GetString() : null;
                        var mapped = Adapters.Convert(source, root.GetProperty("raw"), string.IsNullOrEmpty(eventOverride) ? null : eventOverride);
                        bool hookOk = true;
                        foreach (var e in mapped) hookOk &= state.Apply(e);
                        result = new { ok = hookOk }; break;
                    default: result = new { ok = false, error = "unknown_operation" }; break;
                }
                await Wire.Write(pipe, result, deadline.Token);
            }
            catch (OperationCanceledException) { }
            // InvalidDataException derives from SystemException, not IOException,
            // so it must be listed explicitly. A single malformed delivery must
            // never terminate a pooled worker: eight of them would silently
            // shrink the pool to nothing and freeze the tray.
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or InvalidDataException or NotSupportedException) { Diagnostics.Write("request", e.GetType().Name); }
        }
    }
}

public static class Diagnostics
{
    static readonly object gate = new();
    public static void Write(string category, string detail)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Names.DataDir);
                var path = Path.Combine(Names.DataDir, "diagnostics.log");
                // Bounded log, no payloads, paths, commands, or credentials.
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "Log capacity reached; starting new diagnostic segment.\n");
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {category}: {detail}\n");
            }
        }
        catch { }
    }
}
