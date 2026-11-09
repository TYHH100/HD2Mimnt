using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.IPC;

namespace HD2Mimnt.Tests.Services;

public sealed class PreviewWorkerSessionTests
{
    private static string StubPath => Path.Combine(AppContext.BaseDirectory, "HD2Mimnt.WorkerStub.exe");

    private static PreviewWorkerSession CreateSession() => new(
        workerExecutablePath: StubPath,
        connectTimeout: TimeSpan.FromSeconds(10),
        shutdownGracePeriod: TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Request_EchoesThroughWorkerAndReceivesEvents()
    {
        using var session = CreateSession();
        session.Enter();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.EventReceived += previewEvent =>
        {
            if (previewEvent.EventType == "Test" && previewEvent.Payload is { } payload)
                received.TrySetResult(payload);
        };

        var echoed = await session.RequestAsync<string>("Echo", "hello", CancellationToken.None);
        Assert.Equal("hello", echoed);
        Assert.True(session.IsRunning);

        await session.RequestAsync<string?>("EmitEvent", "x", CancellationToken.None);
        Assert.Equal("\"evt\"", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        await session.ExitAsync();
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Exit_TerminatesOnlyAfterLastUserLeaves()
    {
        using var session = CreateSession();
        session.Enter();
        session.Enter();
        await session.RequestAsync<string>("Echo", "one", CancellationToken.None);

        await session.ExitAsync();
        Assert.True(session.IsRunning);

        await session.ExitAsync();
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Dispose_StopsRunningWorker()
    {
        var session = CreateSession();
        session.Enter();
        await session.RequestAsync<string>("Echo", "one", CancellationToken.None);
        Assert.True(session.IsRunning);

        session.Dispose();
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Request_AfterExit_RestartsWorkerSession()
    {
        using var session = CreateSession();
        session.Enter();
        await session.RequestAsync<string>("Echo", "one", CancellationToken.None);
        await session.ExitAsync();
        Assert.False(session.IsRunning);

        session.Enter();
        var echoed = await session.RequestAsync<string>("Echo", "two", CancellationToken.None);
        Assert.Equal("two", echoed);
        Assert.True(session.IsRunning);
        await session.ExitAsync();
    }

    [Fact]
    public async Task Request_AfterDispose_Throws()
    {
        var session = CreateSession();
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.RequestAsync<string>("Echo", "x", CancellationToken.None));
    }
}
