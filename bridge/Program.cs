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
            try { BridgeStartup.RemoveLegacy(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Diagnostics.Write("legacy-startup", error.GetType().Name); }
            if (command == "serve")
            {
                // A previous login startup entry may still invoke the old bare command.
                // It must not leave an unowned background process running.
                if (args.Length == 1) return 0;
                using var host = HostLifetime.Parse(args);
                using var singleton = new Mutex(true, Names.Lock, out bool created);
                if (!created) return 0;
                new Server().Run(host.Token).GetAwaiter().GetResult();
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
        try { await Wire.Request(new { op = "ping" }, 500); return; }
        catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { }
        // The DLL starts and supervises the bridge. Details must never create an
        // independent process which could keep files locked after the host exits.
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000)
        {
            try { await Wire.Request(new { op = "ping" }, 300); return; }
            catch (Exception error) when (error is OperationCanceledException or IOException or TimeoutException) { await Task.Delay(50); }
        }
        throw new TimeoutException("请启动 TrafficMonitor 并启用 Agent 状态插件");
    }

}
