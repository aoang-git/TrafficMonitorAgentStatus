using System.Diagnostics;
using System.Text.Json;

namespace AgentStatus;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        string command = args.FirstOrDefault() ?? "details";
        try
        {
            if (command == "serve")
            {
                using var singleton = new Mutex(true, Names.Lock, out bool created);
                if (!created) return 0;
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                new Server().Run(cts.Token).GetAwaiter().GetResult();
                return 0;
            }
            if (command == "details")
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new DetailsForm()); return 0;
            }
            if (command == "emit") return Emit(args).GetAwaiter().GetResult();
            if (command == "snapshot" || command == "ping")
            {
                Console.WriteLine(Wire.Request(new { op = command }).GetAwaiter().GetResult().GetRawText()); return 0;
            }
            if (command == "start") { EnsureStarted().GetAwaiter().GetResult(); return 0; }
            Console.WriteLine("AgentStatusBridge: serve | start | details | emit --source SOURCE [--event NAME] | snapshot | ping");
            return 1;
        }
        catch (Exception e)
        {
            Diagnostics.Write(command, e.GetType().Name);
            // Observer hooks never alter authorization or block the agent on collector failure.
            if (command == "emit" && !args.Contains("--strict")) return 0;
            Console.Error.WriteLine(e.Message); return 1;
        }
    }

    public static async Task EnsureStarted()
    {
        try { await Wire.Request(new { op = "ping" }, 100); return; } catch { }
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path");
        Process.Start(new ProcessStartInfo(path, "serve") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory });
        // The caller may return before readiness; this loop has a strict cold-start bound.
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 1500)
        {
            try { await Wire.Request(new { op = "ping" }, 100); return; } catch { await Task.Delay(30); }
        }
        throw new TimeoutException("Collector startup timeout");
    }

    static async Task<int> Emit(string[] args)
    {
        string Option(string key, string fallback = "") { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        string source = Option("--source", "generic");
        // Bound stdin before JSON parsing. A hostile or broken sender cannot grow memory indefinitely.
        using var deadline = new CancellationTokenSource(2000);
        using var input = Console.OpenStandardInput();
        using var data = new MemoryStream();
        byte[] chunk = new byte[4096];
        int n;
        while ((n = await input.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (data.Length + n > Wire.MaxBytes) throw new InvalidDataException("Hook payload exceeds 64 KiB");
            data.Write(chunk, 0, n);
        }
        var raw = JsonSerializer.Deserialize<JsonElement>(data.ToArray());
        string name = Option("--event");
        var events = Adapters.Convert(source, raw, name.Length == 0 ? null : name);
        if (events.Count == 0) return 0;
        await EnsureStarted();
        var result = await Wire.Request(new { op = "emit", events });
        if (!result.GetProperty("ok").GetBoolean()) Diagnostics.Write("emit", "rejected");
        return 0;
    }
}
