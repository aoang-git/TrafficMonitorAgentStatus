using Microsoft.Win32;

namespace AgentStatus;

internal static class BridgeStartup
{
    internal const string ValueName = "TrafficMonitorAgentStatus";

    public static void Enable()
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new IOException("无法打开当前用户启动项");
        Register(run, Path.Combine(AppContext.BaseDirectory, "AgentStatusBridge.exe"));
    }

    internal static void Register(RegistryKey run, string executable)
    {
        string path = Path.GetFullPath(executable);
        if (!File.Exists(path)) throw new FileNotFoundException("未找到后台桥接程序");
        string command = "\"" + path + "\" serve";
        if (run.GetValue(ValueName) is string current && string.Equals(current, command, StringComparison.OrdinalIgnoreCase)
            && run.GetValueKind(ValueName) == RegistryValueKind.String) return;
        run.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
