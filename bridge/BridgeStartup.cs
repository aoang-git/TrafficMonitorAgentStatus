using Microsoft.Win32;

namespace AgentStatus;

internal static class BridgeStartup
{
    internal const string ValueName = "TrafficMonitorAgentStatus";

    public static void RemoveLegacy()
    {
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (run != null) RemoveLegacy(run);
    }

    internal static bool RemoveLegacy(RegistryKey run)
    {
        if (run.GetValue(ValueName) is not string command) return false;
        var match = System.Text.RegularExpressions.Regex.Match(command, "^\"([^\"\\r\\n]+)\" serve$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !Path.IsPathFullyQualified(match.Groups[1].Value)
            || !string.Equals(Path.GetFileName(match.Groups[1].Value), "AgentStatusBridge.exe", StringComparison.OrdinalIgnoreCase)) return false;
        run.DeleteValue(ValueName, false);
        return true;
    }
}
