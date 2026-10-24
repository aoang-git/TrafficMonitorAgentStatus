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
            if (command is "snapshot" or "ping" or "stop")
            {
                Console.WriteLine(Wire.Request(new { op = command }).GetAwaiter().GetResult().GetRawText()); return 0;
            }
            if (command == "start") { EnsureStarted().GetAwaiter().GetResult(); return 0; }
            Console.WriteLine("AgentStatusBridge: serve | start | details | snapshot | ping | stop");
            return 1;
        }
        catch (Exception e)
        {
            Diagnostics.Write(command, e.GetType().Name);
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

}
