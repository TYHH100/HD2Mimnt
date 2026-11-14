using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class HellForgeSessionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-HfSession-{Guid.NewGuid():N}");
    private readonly HellForgeCliFixture _fixture = HellForgeCliFixture.Create();
    private readonly SettingsService _settings;
    private readonly HellForgeSessionService _service;

    public HellForgeSessionServiceTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(_root);
        _fixture.ConfigureAsync(_settings).GetAwaiter().GetResult();
        _service = new HellForgeSessionService(new HellForgeService(_settings));
    }

    private const string HappyStream = """
        {"event":"started","runId":"run1","run":"stub/run1","pid":123,"profile":"default","war":"live","arguments":""}
        {"event":"status","status":"preparing","failure":null}
        {"event":"status","status":"patching","failure":null}
        {"event":"status","status":"observing","failure":null}
        {"event":"ended","status":"observed","result":"stub","logs":"stub"}
        """;

    [Fact]
    public async Task StartAsync_ShouldStreamPhasesUntilEnded()
    {
        _fixture.SetPlayStream(HappyStream);
        var phases = new List<HellForgeSessionPhase>();
        _service.StateChanged += (_, _) => phases.Add(_service.State.Phase);

        await _service.StartAsync();
        Assert.Equal(HellForgeSessionPhase.Preparing, _service.State.Phase);
        Assert.True(_service.State.Tracked);
        Assert.Equal("run1", _service.State.RunId);

        await WaitForPhaseAsync(HellForgeSessionPhase.Ended);
        Assert.Contains(HellForgeSessionPhase.Patching, phases);
        Assert.Contains(HellForgeSessionPhase.Running, phases);
        Assert.False(_service.State.IsActive);
        Assert.Contains("play --wait yes", _fixture.ReadArgs());
    }

    [Fact]
    public async Task StartAsync_WhenCliRefuses_ShouldThrowWithMessage()
    {
        _fixture.SetFailure("HellForge: A HellForge session is running already: stop it first (stop).");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.StartAsync());
        Assert.Contains("running already", error.Message);
        Assert.False(_service.State.IsActive);
    }

    [Fact]
    public async Task StartAsync_WhenRunnerDiesBeforeStarted_ShouldThrow()
    {
        _fixture.SetPlayStream("", exitCode: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.StartAsync());
        Assert.False(_service.State.IsActive);
    }

    [Fact]
    public async Task FailedStatus_ShouldSurfaceFailureAndKeepStep()
    {
        _fixture.SetPlayStream("""
            {"event":"started","runId":"run2","run":"stub/run2","pid":1,"profile":"default","war":"live","arguments":""}
            {"event":"status","status":"preparing","failure":null}
            {"event":"status","status":"failed","failure":"The game copy is not complete."}
            {"event":"ended","status":"failed","result":"stub","logs":"stub"}
            """);
        await _service.StartAsync();
        await WaitForPhaseAsync(HellForgeSessionPhase.Failed);
        Assert.Equal("The game copy is not complete.", _service.State.Failure);
    }

    [Fact]
    public async Task StopAsync_ShouldSignalStopAndRefresh()
    {
        _fixture.SetPlayStream("""
            {"event":"started","runId":"run3","run":"stub/run3","pid":1,"profile":"default","war":"live","arguments":""}
            {"event":"status","status":"observing","failure":null}
            """, hold: true);
        await _service.StartAsync();
        await WaitForPhaseAsync(HellForgeSessionPhase.Running);

        await _service.StopAsync();
        Assert.Contains("stop --wait yes", _fixture.ReadArgs());
        await WaitForPhaseAsync(HellForgeSessionPhase.Ended);
    }

    [Fact]
    public async Task RefreshAsync_ExternalRunningSession_ShouldShowRunningUntracked()
    {
        _fixture.OverwriteResponse("status", """
            { "hellforge": "0.4.0-beta", "ready": { "gameCopy": true, "sessionRunner": true },
              "settings": { "profile": "default" },
              "session": { "running": true, "lastRun": "stub/external", "lastStatus": null, "lastFailure": null } }
            """);
        await _service.RefreshAsync();
        Assert.Equal(HellForgeSessionPhase.Running, _service.State.Phase);
        Assert.False(_service.State.Tracked);
    }

    private async Task WaitForPhaseAsync(HellForgeSessionPhase phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (_service.State.Phase != phase)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeout.Token);
        }
    }

    public void Dispose()
    {
        _service.Dispose();
        _fixture.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}