using Microsoft.Win32.SafeHandles;

namespace AgentStatus;

internal sealed class HostLifetime : IDisposable
{
    sealed class InheritedHandle(nint value) : WaitHandle
    {
        public void Initialize() => SafeWaitHandle = new SafeWaitHandle(value, true);
    }
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    readonly InheritedHandle process, plugin;
    readonly RegisteredWaitHandle? processWait, pluginWait;
    bool disposed;
    public CancellationToken Token => stop.Token;

    HostLifetime(nint processHandle, nint pluginHandle)
    {
        process = new(processHandle); process.Initialize();
        plugin = new(pluginHandle); plugin.Initialize();
        try
        {
            processWait = ThreadPool.RegisterWaitForSingleObject(process, (_, _) => Cancel(), null, Timeout.Infinite, true);
            pluginWait = ThreadPool.RegisterWaitForSingleObject(plugin, (_, _) => Cancel(), null, Timeout.Infinite, true);
        }
        catch { Dispose(); throw; }
    }
    public static HostLifetime Parse(string[] args)
    {
        if (args.Length != 5 || args[0] != "serve" || args[1] != "--host-process-handle" || args[3] != "--plugin-stop-handle"
            || !long.TryParse(args[2], out long process) || process <= 0
            || !long.TryParse(args[4], out long plugin) || plugin <= 0 || process == plugin)
            throw new ArgumentException("后台连接程序需由 TrafficMonitor 插件启动");
        return new((nint)process, (nint)plugin);
    }
    void Cancel() { lock (gate) { if (!disposed) stop.Cancel(); } }
    public void Dispose()
    {
        lock (gate) { disposed = true; }
        processWait?.Unregister(null); pluginWait?.Unregister(null);
        process.Dispose(); plugin.Dispose(); stop.Dispose();
    }
}
