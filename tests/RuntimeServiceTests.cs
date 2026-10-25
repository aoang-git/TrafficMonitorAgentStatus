using AgentStatus;

static class RuntimeServiceTests
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        async Task Fails(Func<Task> action, string message, string name)
        {
            try { await action(); }
            catch (RuntimeServiceException error) { check(error.Message.Contains(message), name); return; }
            throw new Exception("FAIL: " + name);
        }
        var missingDir = Path.Combine(root, "missing-service");
        var executable = Path.Combine(missingDir, "agent-statusd.exe");
        int starts = 0;
        FakeRuntime? lateService = null;
        using var service = new RuntimeService(missingDir, executable, _ =>
        {
            starts++; lateService = new FakeRuntime(missingDir); return Task.CompletedTask;
        });
        await Fails(async () => await service.Discover(default), "未发现Runtime服务", "no installation shows exact missing-service message");
        Directory.CreateDirectory(missingDir); await File.WriteAllTextAsync(executable, "test-only executable marker");
        try
        {
            var connection = await service.Discover(default);
            check(starts == 1 && connection.Epoch == lateService!.Epoch, "installation appearing later starts and connects without manual configuration");
            await service.Discover(default); check(starts == 1, "healthy service is reused without repeated starts");
            lateService!.InfoSchema = 4;
            await Fails(async () => await service.Discover(default), "协议不兼容", "incompatible running service is identified instead of restarted");
            check(starts == 1, "protocol mismatch never starts another service");
        }
        finally { if (lateService != null) await lateService.DisposeAsync(); }

        var failedDir = Path.Combine(root, "failed-service");
        int failedStarts = 0;
        using var failed = new RuntimeService(failedDir, executable, _ =>
        { failedStarts++; throw new IOException("synthetic startup failure"); });
        await Fails(async () => await failed.Discover(default), "启动失败", "startup failure differs from missing installation");
        await Fails(async () => await failed.Discover(default), "未连接", "background retries do not continuously restart stopped service");
        check(failedStarts == 1, "only one start attempt per discovery request");
        failed.RetryStart();
        await Fails(async () => await failed.Discover(default), "启动失败", "manual discovery permits a new start attempt");
        check(failedStarts == 2, "explicit retry starts once again");

        using var timeout = new RuntimeService(Path.Combine(root, "timeout-service"), executable,
            _ => Task.CompletedTask, startupTimeout: TimeSpan.FromMilliseconds(100));
        await Fails(async () => await timeout.Discover(default), "连接超时", "startup command success still waits for compatible service readiness");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledCorrectly = false;
        try { await timeout.Discover(cancelled.Token); } catch (OperationCanceledException) { cancelledCorrectly = true; }
        check(cancelledCorrectly, "service discovery respects cancellation");

        await using var running = new FakeRuntime(Path.Combine(root, "running-service"));
        using var runningService = new RuntimeService(running.Directory, Path.Combine(root, "absent.exe"));
        var live = await runningService.Discover(default);
        check(live.Epoch == running.Epoch, "healthy connection is accepted even without executable at standard location");
    }
}
