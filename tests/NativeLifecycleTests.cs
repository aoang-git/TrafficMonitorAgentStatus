using AgentStatus;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

static class NativeLifecycleTests
{
    public static async Task Run(string root, string buildDirectory, Action<bool, string> check)
    {
        string fixture = Path.Combine(root, "copied plugin host"), bridgeDirectory = Path.Combine(fixture, "AgentStatus");
        Directory.CreateDirectory(bridgeDirectory);
        string dll = Path.Combine(fixture, "AgentStatus.dll");
        File.Copy(Path.Combine(Path.GetFullPath(buildDirectory), "AgentStatus.dll"), dll);
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(bridgeDirectory, Path.GetFileName(file)));
        string executable = Path.Combine(bridgeDirectory, "AgentStatusBridge.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "StateTests.exe"), executable);
        await using var runtime = new FakeRuntime(Path.Combine(root, "native-lifecycle-runtime"));
        var info = new ProcessStartInfo(Path.Combine(Path.GetFullPath(buildDirectory), "PluginProbe.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = fixture
        };
        info.ArgumentList.Add(dll); info.ArgumentList.Add("--lifecycle");
        info.Environment["TMAS_STARTUP_TEST_RUNTIME"] = runtime.Directory;
        info.Environment["TMAS_DATA_DIR"] = Path.Combine(root, "native-lifecycle-state");
        using var host = Process.Start(info)!;
        var errors = host.StandardError.ReadToEndAsync();
        var bridges = new List<Process>();
        async Task<string?> Read() => await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        async Task<string?> Command(string command)
        { await host.StandardInput.WriteLineAsync(command); await host.StandardInput.FlushAsync(); return await Read(); }
        async Task<Process> Connected(int previousSubscriptions)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 15000)
            {
                await Command("poll");
                if (runtime.Subscriptions > previousSubscriptions)
                {
                    try
                    {
                        var snapshot = (await Wire.Request(new { op = "snapshot" }, 1000)).Deserialize<Snapshot>(JsonWire.Options)!;
                        if (snapshot.Connected)
                        {
                            var ping = await Wire.Request(new { op = "ping" }, 1000);
                            var child = Process.GetProcessById(ping.GetProperty("pid").GetInt32());
                            if (!string.Equals(child.MainModule!.FileName, executable, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected bridge path");
                            bridges.Add(child); return child;
                        }
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
                }
                await Task.Delay(100);
            }
            throw new Exception("Native host did not auto-connect. " + (host.HasExited ? await errors : ""));
        }
        try
        {
            check(await Read() == "LOADED", "copied native plugin loads without an installer or discovery click");
            var first = await Connected(0);
            check(!first.HasExited, "DLL automatically starts a bridge tied to the native host");
            await runtime.Emit("working");
            string? value = null; var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 5000)
            {
                value = await Command("poll");
                if (value == "value=Codex:工作中") break;
                await Task.Delay(30);
            }
            check(value == "value=Codex:工作中", "new Runtime activity reaches native display without setup UI");
            int subscriptions = runtime.Subscriptions;
            first.Kill(); await first.WaitForExitAsync();
            var recovered = await Connected(subscriptions);
            check(recovered.Id != first.Id, "native host automatically recovers a terminated bridge");
            check(await Command("unload") == "UNLOADED", "native plugin can unload while its host stays running");
            await recovered.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(!host.HasExited && recovered.ExitCode == 0, "unloading plugin gracefully stops bridge before host exit");
            subscriptions = runtime.Subscriptions;
            check(await Command("load") == "LOADED", "native plugin reloads after unloading");
            var restarted = await Connected(subscriptions);
            host.Kill(); await host.WaitForExitAsync();
            await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(restarted.ExitCode == 0, "hard host termination automatically stops inherited bridge lifetime");
            using var writableBridge = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var writableDll = new FileStream(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            check(true, "host exit releases bridge and plugin files for overwrite upgrades");
        }
        finally
        {
            if (!host.HasExited) { host.Kill(); await host.WaitForExitAsync(); }
            foreach (var child in bridges)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                child.Dispose();
            }
        }
    }
}
