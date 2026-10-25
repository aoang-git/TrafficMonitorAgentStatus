using System.Diagnostics;

namespace AgentStatus;

public sealed class RuntimeServiceException(string message) : IOException(message);

// Only the current user's standard installation is started. No PATH or repository search.
public sealed class RuntimeService : IDisposable
{
    public string Directory { get; }
    readonly string executable;
    readonly Func<CancellationToken, Task> start;
    readonly RuntimeClient http;
    readonly TimeSpan startupTimeout;
    int attemptedStart;

    public RuntimeService(string? directory = null, string? executable = null,
        Func<CancellationToken, Task>? start = null, RuntimeClient? client = null, TimeSpan? startupTimeout = null)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentStatusRuntime");
        this.executable = executable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "AgentStatusRuntime", "agent-statusd.exe");
        this.start = start ?? StartInstalled;
        http = client ?? new RuntimeClient();
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(15);
    }

    public void RetryStart() => Interlocked.Exchange(ref attemptedStart, 0);

    static bool ConnectionFailure(Exception error) => error is IOException or HttpRequestException
        or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException or TimeoutException;

    async Task<RuntimeConnection> Probe(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        RuntimeConnection connection;
        RuntimeInfo info;
        try
        {
            connection = await RuntimeConnection.LoadAsync(Directory, timeout.Token);
            info = await http.GetAsync<RuntimeInfo>(connection, "/v3/info", timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Runtime probe timed out"); }
        if (info.Schema != 3 || info.Mode != "facts")
            throw new RuntimeServiceException("Runtime 服务协议不兼容，请更新 Runtime");
        if (info.Epoch != connection.Epoch) throw new IOException("Runtime instance changed");
        return connection;
    }

    public async Task<RuntimeConnection> Discover(CancellationToken ct)
    {
        try { return await Probe(ct); }
        catch (RuntimeServiceException) { throw; }
        catch (Exception error) when (ConnectionFailure(error)) { }
        if (!File.Exists(executable)) throw new RuntimeServiceException("未发现Runtime服务");
        if (Interlocked.Exchange(ref attemptedStart, 1) != 0)
            throw new RuntimeServiceException("Runtime 服务未连接，正在重试；可点击“发现服务并连接”重新尝试启动");
        try { await start(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (ConnectionFailure(error) || error is System.ComponentModel.Win32Exception or OperationCanceledException)
        { throw new RuntimeServiceException("Runtime 服务启动失败，请检查安装和权限"); }

        using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ready.CancelAfter(startupTimeout);
        try
        {
            while (true)
            {
                try { return await Probe(ready.Token); }
                catch (RuntimeServiceException) { throw; }
                catch (Exception error) when (ConnectionFailure(error)) { }
                await Task.Delay(200, ready.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new RuntimeServiceException("Runtime 服务启动后连接超时，请检查服务运行情况"); }
    }

    async Task StartInstalled(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = Process.Start(new ProcessStartInfo(executable, "start")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        }) ?? throw new IOException("Runtime start failed");
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); throw; }
        if (process.ExitCode != 0) throw new IOException("Runtime start failed");
    }

    public void Dispose() => http.Dispose();
}
