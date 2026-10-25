using AgentStatus;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;

static class BridgeStartupTests
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        string keyPath = @"Software\TrafficMonitorAgentStatus.Tests\" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, "copied plugin", "AgentStatus");
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "AgentStatusBridge.exe");
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(keyPath, true)!)
            {
                run.SetValue("UnrelatedApp", "keep");
                bool missingRejected = false;
                try { BridgeStartup.Register(run, executable); } catch (FileNotFoundException) { missingRejected = true; }
                check(missingRejected && run.GetValue(BridgeStartup.ValueName) == null, "missing bridge cannot register a broken startup command");
                File.WriteAllText(executable, "isolated executable marker");
                BridgeStartup.Register(run, executable);
                string expected = "\"" + executable + "\" serve";
                check((string?)run.GetValue(BridgeStartup.ValueName) == expected
                    && run.GetValueKind(BridgeStartup.ValueName) == RegistryValueKind.String,
                    "copied plugin registers quoted installed bridge path for login startup");
                BridgeStartup.Register(run, executable);
                check(run.GetValueNames().Length == 2 && (string?)run.GetValue("UnrelatedApp") == "keep",
                    "repeated discovery keeps one startup entry and preserves unrelated values");
                run.SetValue(BridgeStartup.ValueName, "\"C:\\old-location\\AgentStatusBridge.exe\" serve");
                BridgeStartup.Register(run, executable);
                check((string?)run.GetValue(BridgeStartup.ValueName) == expected, "discovery repairs startup after moving the copied plugin");
                run.SetValue(BridgeStartup.ValueName, "old");
            }
            using var readOnly = Registry.CurrentUser.OpenSubKey(keyPath, false)!;
            bool denied = false;
            try { BridgeStartup.Register(readOnly, executable); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException) { denied = true; }
            check(denied, "startup write failure is reported rather than silently accepted");

            await using var runtime = new FakeRuntime(Path.Combine(root, "startup-runtime"));
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
                if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "StateTests.exe"), executable, true);
            using (var run = Registry.CurrentUser.OpenSubKey(keyPath, true)!) BridgeStartup.Register(run, executable);
            using (var run = Registry.CurrentUser.OpenSubKey(keyPath, false)!)
                check((string?)run.GetValue(BridgeStartup.ValueName) == "\"" + executable + "\" serve", "login command points at the copied bridge rather than an installer");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory };
            info.ArgumentList.Add("serve");
            info.Environment["TMAS_STARTUP_TEST_RUNTIME"] = runtime.Directory;
            info.Environment["TMAS_DATA_DIR"] = Path.Combine(root, "startup-state");
            using var child = Process.Start(info)!;
            try
            {
                await runtime.WaitFor(() => runtime.LastSubscribedEpoch == runtime.Epoch);
                var ping = await Wire.Request(new { op = "ping" }, 2000);
                check(ping.GetProperty("pid").GetInt32() == child.Id, "login-launched bridge connects automatically without another discovery click");
                await runtime.Emit("working");
                Snapshot? snapshot = null;
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 5000)
                {
                    snapshot = (await Wire.Request(new { op = "snapshot" }, 2000)).Deserialize<Snapshot>(JsonWire.Options)!;
                    if (snapshot.Text == "Codex:工作中") break;
                    await Task.Delay(30);
                }
                check(snapshot?.Connected == true && snapshot.Text == "Codex:工作中", "login startup delivers new Runtime activity without installation script or UI");
                await Wire.Request(new { op = "stop" }, 2000);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check(child.ExitCode == 0, "login roundtrip stops its isolated bridge cleanly");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }
}
