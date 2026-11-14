using System.IO.Compression;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class HellForgeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-HfSvc-{Guid.NewGuid():N}");
    private readonly HellForgeCliFixture _fixture = HellForgeCliFixture.Create();
    private readonly SettingsService _settings;
    private readonly HellForgeService _service;

    public HellForgeServiceTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(_root);
        _fixture.ConfigureAsync(_settings).GetAwaiter().GetResult();
        _service = new HellForgeService(_settings);
    }

    [Fact]
    public async Task GetStatusAsync_ShouldParseVersionsReadinessAndSession()
    {
        var status = await _service.GetStatusAsync();
        Assert.Equal("0.4.0-beta", status.Hellforge);
        Assert.Equal("25480438", status.GameBuild);
        Assert.Equal("1.2.3", status.Engine);
        Assert.Equal(16, status.ModSdk);
        Assert.True(status.Ready.GameCopy);
        Assert.True(status.Ready.SessionRunner);
        Assert.Equal("wfp", status.NetworkBlock);
        Assert.Equal("default", status.Settings.Profile);
        Assert.False(status.Session.Running);
        Assert.Equal("observed", status.Session.LastStatus);
    }

    [Fact]
    public async Task GetProfilesAsync_ShouldParseProfilesAndTemplates()
    {
        var result = await _service.GetProfilesAsync();
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal("default", result.Profiles[0].Name);
        Assert.True(result.Profiles[0].Selected);
        Assert.True(result.Profiles[0].Used);
        Assert.Equal("alt", result.Profiles[1].Name);
        Assert.False(result.Profiles[1].Selected);
        var template = Assert.Single(result.Templates);
        Assert.Equal("starter", template.Name);
        Assert.Equal(3, template.Items);
    }

    [Fact]
    public async Task CreateProfileAsync_ShouldPassNameAndTemplate()
    {
        await _service.CreateProfileAsync("new-run", "starter");
        var args = _fixture.ReadArgs();
        Assert.Contains("profiles --create new-run --template starter", args);
        Assert.Contains("--install", args);
    }

    [Fact]
    public async Task SelectProfileAsync_ShouldPassProfileOption()
    {
        await _service.SelectProfileAsync("alt");
        Assert.Contains("settings --profile alt", _fixture.ReadArgs());
    }

    [Fact]
    public async Task CliFailure_ShouldSurfaceStderrMessage()
    {
        _fixture.SetFailure("HellForge: profile name is taken.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetProfilesAsync());
        Assert.Contains("profile name is taken", error.Message);
    }

    [Fact]
    public async Task MalformedJson_ShouldThrowParseError()
    {
        _fixture.OverwriteResponse("status", "{ not json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetStatusAsync());
    }

    [Fact]
    public async Task MissingInstallation_ShouldThrowBeforeStartingCli()
    {
        await _settings.SetSettingAsync(SettingKeys.HellForgePath, string.Empty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetStatusAsync());
        Assert.False(File.Exists(_fixture.ArgsFile));
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldParseSettings()
    {
        var settings = await _service.GetSettingsAsync();
        Assert.Equal("default", settings.Profile);
        Assert.Equal("live", settings.War);
        Assert.True(settings.LocalPlayFab);
        Assert.True(settings.Sandbox);
        Assert.True(settings.SteamBridge);
        Assert.Null(settings.PlayWithStrangersChosen);
        Assert.Equal(30, settings.ForgeCreditChance);
        Assert.EndsWith("launcher-settings.json", settings.File);
    }

    [Fact]
    public async Task UpdateSettingsAsync_ShouldSendGivenOptionsAndReturnSavedSettings()
    {
        var saved = await _service.UpdateSettingsAsync(new Dictionary<string, string>
        {
            ["war"] = "frozen",
            ["sandbox"] = "no",
            ["strangers"] = "no",
            ["forge-credits"] = "45"
        });
        Assert.Contains("settings --war frozen --sandbox no --strangers no --forge-credits 45", _fixture.ReadArgs());
        Assert.Equal("frozen", saved.War);
        Assert.False(saved.Sandbox);
        Assert.False(saved.PlayWithStrangersChosen);
        Assert.False(saved.PlayWithStrangers);
        Assert.Equal(45, saved.ForgeCreditChance);
    }

    [Fact]
    public async Task GetLogsAsync_ShouldListRunsNewestFirstAndFilesForRun()
    {
        CreateRun("runA", """{"status":"completed","gameExitCode":"0x00000000","startedUtc":"2026-11-11T08:00:00Z"}""",
            ("session.log", "a"));
        CreateRun("runB", """{"status":"crashed","failure":"access violation","crashed":true}""",
            ("engine-logs/console.log", "bb"));

        var logs = await _service.GetLogsAsync();
        Assert.Equal(new[] { "runB", "runA" }, logs.Runs.Select(r => r.Id));
        var crashed = logs.Runs[0];
        Assert.True(crashed.Crashed);
        Assert.Equal("access violation", crashed.Failure);
        Assert.Equal("completed", logs.Runs[1].Status);
        Assert.Equal("0x00000000", logs.Runs[1].GameExitCode);
        Assert.Equal(new DateTime(2026, 11, 11, 8, 0, 0, DateTimeKind.Utc), logs.Runs[1].StartedUtc);
        Assert.Null(logs.Files);

        var one = await _service.GetLogsAsync("runB");
        Assert.Contains("logs --run runB", _fixture.ReadArgs());
        Assert.Contains(one.Files!, file => file.Path == "engine-logs/console.log" && file.Bytes == 2);
    }

    [Fact]
    public async Task ExportLogsAsync_ShouldZipSelectedRunAndReplaceExistingFile()
    {
        CreateRun("run1", null, ("session.log", "one"), ("engine-logs/console.log", "two"));
        var zip = Path.Combine(_root, "export", "logs.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.WriteAllText(zip, "stale");

        var result = await _service.ExportLogsAsync(zip, "run1");
        Assert.Contains("logs --run run1 --export", _fixture.ReadArgs());
        Assert.Equal(2, result.Files);
        Assert.Equal("run1", result.Run!.Id);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(new[] { "engine-logs/console.log", "session.log" },
            archive.Entries.Select(e => e.FullName).Order());
    }

    [Fact]
    public async Task CreateCrashReportAsync_ShouldIncludeDumpOnlyWhenAsked()
    {
        CreateRun("crash-run", """{"status":"crashed","crashed":true}""", ("session.log", "log"), ("crash.dmp", "dump"));
        var zip = Path.Combine(_root, "report.zip");

        var result = await _service.CreateCrashReportAsync(zip, dump: true, runId: "crash-run");
        Assert.Contains("logs --run crash-run --crash-report", _fixture.ReadArgs());
        Assert.Contains("--dump yes", _fixture.ReadArgs());
        Assert.True(result.Dump);
        Assert.True(result.DumpAvailable);
        Assert.Equal("https://github.com/hellforge/hellforge/issues/new", result.IssuePage);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).Order().ToArray();
        Assert.Contains("summary.txt", names);
        Assert.Contains("logs/session.log", names);
        Assert.Contains("dump/crash-1.dmp", names);
    }

    [Fact]
    public async Task ExportLogsAsync_WithNoSessions_ShouldSurfaceCliRefusal()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ExportLogsAsync(Path.Combine(_root, "none.zip")));
        Assert.Contains("No session logs yet", error.Message);
    }

    private string CreateRun(string id, string? runJson, params (string Name, string Content)[] files)
    {
        var folder = Path.Combine(_fixture.Root, "logs", "sessions", id);
        Directory.CreateDirectory(folder);
        if (runJson != null) File.WriteAllText(Path.Combine(folder, "run.json"), runJson);
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return folder;
    }

    public void Dispose()
    {
        _fixture.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}