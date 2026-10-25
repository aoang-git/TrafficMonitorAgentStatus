using AgentStatus;
using Microsoft.Win32;

static class BridgeStartupTests
{
    public static void Run(Action<bool, string> check)
    {
        string keyPath = @"Software\TrafficMonitorAgentStatus.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(keyPath, true)!;
            run.SetValue("UnrelatedApp", "keep");
            check(!BridgeStartup.RemoveLegacy(run) && run.GetValueNames().Length == 1, "fresh copied installation creates no independent bridge startup");
            run.SetValue(BridgeStartup.ValueName, "\"C:\\copied plugin\\AgentStatus\\AgentStatusBridge.exe\" serve");
            check(BridgeStartup.RemoveLegacy(run) && run.GetValue(BridgeStartup.ValueName) == null
                && (string?)run.GetValue("UnrelatedApp") == "keep", "upgrade removes only the owned legacy bridge login command");
            run.SetValue(BridgeStartup.ValueName, "\"C:\\OtherApp.exe\" serve");
            check(!BridgeStartup.RemoveLegacy(run) && run.GetValue(BridgeStartup.ValueName) != null, "startup migration preserves unrelated executable commands");
            bool invalid = false;
            try { using var host = HostLifetime.Parse(new[] { "serve" }); } catch (ArgumentException) { invalid = true; }
            check(invalid, "unowned bridge lifetime arguments are rejected");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }
}
