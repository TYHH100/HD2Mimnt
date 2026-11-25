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
        Assert.False(status.Ready.LinuxNamespace);
        Assert.Equal(new[] { "English" }, status.Languages);
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
    public async Task ModernSettings_ShouldParseAndSaveWarAndSharingOptions()
    {
        _fixture.EnableModernCli();
        var saved = await _service.UpdateSettingsAsync(new Dictionary<string, string>
        {
            ["war"] = "hellforge",
            ["war-server"] = "https://war.example.test",
            ["deal-subfactions"] = "no",
            ["deal-seaf"] = "yes",
            ["war-seed"] = "12345",
            ["share-mods"] = "no",
            ["list-public"] = "yes",
            ["show-edited-stats"] = "yes",
            ["host-stats"] = "no"
        });
        Assert.Contains("settings --war hellforge --war-server https://war.example.test --deal-subfactions no", _fixture.ReadArgs());
        Assert.Contains("--war-seed 12345 --share-mods no --list-public yes --show-edited-stats yes --host-stats no", _fixture.ReadArgs());
        Assert.Equal("hellforge", saved.War);
        Assert.Equal("https://war.example.test", saved.WarServer);
        Assert.False(saved.DealSubfactions);
        Assert.True(saved.DealSeaf);
        Assert.Equal(12345, saved.WarSeed);
        Assert.False(saved.ShareMods);
        Assert.True(saved.ListPublic);
        Assert.True(saved.ShowEditedStats);
        Assert.False(saved.HostStats);
    }

    [Fact]
    public async Task Versions_ShouldParseInstalledAndPlayingVersion()
    {
        _fixture.EnableModernCli();
        var versions = await _service.GetVersionsAsync();
        Assert.NotNull(versions);
        Assert.Equal("0.5.0-beta", versions!.Current!.Version);
        Assert.Equal("0.5.0-beta", versions.Playing!.Version);
        Assert.Equal("test", versions.Playing.Commit);
    }

    [Fact]
    public async Task Mods_ShouldParseUnsupportedPremiumAndSourceBuild()
    {
        _fixture.OverwriteResponse("mods", """
            {"mods":[{"id":"owner.source","name":"Source mod","version":"1.2.3","enabled":false,"group":"gameplay",
                      "tags":["source","code"],"removable":true,"unsupported":"needs a newer engine",
                      "premium":[{"requirement":"owns_dlc","name":"Premium armour"}],"url":"https://example.test/mod",
                      "icon":"icon.png","build":{"state":"did-not-compile","built":null,"error":"compile failed"}}],
             "problems":[],"modlist":null,"settings":"stub/mods/hellforge-mods.json"}
            """);
        var mod = Assert.Single((await _service.GetModsAsync()).Mods);
        Assert.Equal("needs a newer engine", mod.Unsupported);
        Assert.Equal("Premium armour", Assert.Single(mod.Premium!).Name);
        Assert.Equal("https://example.test/mod", mod.Url);
        Assert.Equal("did-not-compile", mod.Build!.State);
        Assert.Equal("compile failed", mod.Build.Error);
    }

    [Fact]
    public async Task ModSyncOffer_ShouldReadAndAnswerWithOfferVersion()
    {
        _fixture.EnableModernCli();
        _fixture.OverwriteResponse("mod-sync-offer", """
            {"run":"stub/logs/sessions/run1","inGamePrompt":false,"sent":null,
             "offer":{"version":3,"state":"offer","host":"Host","stranger":true,"live":false,"bytes":1024,"done":0,
                      "message":"Host plays with a mod you do not have.","answers":["accept","decline"],
                      "mods":[{"id":"owner.mod","name":"Mod","version":"1.0","class":"code","action":"trust",
                               "bytes":1024,"url":"https://example.test/mod","answers":["trust","open"],
                               "trustWarning":"Runs code on your PC."}],"trustNote":"Files are checked."}}
            """);
        var current = await _service.GetModSyncOfferAsync();
        Assert.NotNull(current);
        Assert.Equal(3, current!.Offer!.Version);
        Assert.True(Assert.Single(current.Offer.Mods).CanTrust);

        _fixture.OverwriteResponse("mod-sync-answer-result", """
            {"run":"stub/logs/sessions/run1","inGamePrompt":false,"sent":"trust","offer":null}
            """);
        var answered = await _service.AnswerModSyncOfferAsync("trust", 3, "owner.mod");
        Assert.Equal("trust", answered.Sent);
        Assert.Null(answered.Offer);
        Assert.Contains("mod-sync-offer --answer trust --version 3 --mod owner.mod", _fixture.ReadArgs());
    }

    [Fact]
    public async Task Planets_ShouldParseCatalogueAndExplicitSubfactions()
    {
        _fixture.EnableModernCli();
        var result = await _service.UpdatePlanetAsync("Malevelon Creek", "automatons", ["jet-brigade"],
            new Dictionary<string, string> { ["hulks"] = "surge", ["tanks"] = "none" });
        Assert.Contains("planets --planet Malevelon Creek --catalogue yes --faction automatons --subfactions jet-brigade", _fixture.ReadArgs());
        Assert.Contains("--enemies hulks=surge,tanks=none", _fixture.ReadArgs());
        var planet = Assert.Single(result.Planets);
        Assert.Equal(196, planet.Index);
        Assert.Equal("automatons", planet.Faction);
        Assert.Equal(new[] { "jet-brigade" }, planet.SubfactionKeys);
        Assert.Equal("surge", planet.Enemies["hulks"]);
        Assert.Equal("none", planet.Enemies["tanks"]);
        Assert.Contains(result.Catalogue!.Factions, faction => faction == "illuminate");
        Assert.Contains(result.Catalogue.Enemies, enemy => enemy.Key == "hulks");
    }

    [Fact]
    public async Task Planets_ShouldDistinguishWarSubfactionsFromNoneAndClearEnemyOverride()
    {
        _fixture.EnableModernCli();
        await _service.UpdatePlanetAsync("Malevelon Creek", "automatons", ["jet-brigade"],
            new Dictionary<string, string> { ["hulks"] = "surge" });
        var result = await _service.UpdatePlanetAsync("Malevelon Creek", "automatons", null,
            new Dictionary<string, string> { ["hulks"] = "normal" });
        Assert.Contains("--subfactions war", _fixture.ReadArgs());
        Assert.Contains("--enemies hulks=normal", _fixture.ReadArgs());
        var planet = Assert.Single(result.Planets);
        Assert.True(planet.UsesWarSubfactions);
        Assert.Empty(planet.SubfactionKeys);
        Assert.False(planet.Enemies.ContainsKey("hulks"));

        result = await _service.UpdatePlanetAsync("Malevelon Creek", "automatons", [],
            new Dictionary<string, string>());
        Assert.Contains("--subfactions none", _fixture.ReadArgs());
        Assert.False(Assert.Single(result.Planets).UsesWarSubfactions);
        Assert.Empty(Assert.Single(result.Planets).SubfactionKeys);
    }

    [Fact]
    public async Task Planets_ShouldResetOneAndAllOverrides()
    {
        _fixture.EnableModernCli();
        await _service.UpdatePlanetAsync("Malevelon Creek", "automatons", ["jet-brigade"],
            new Dictionary<string, string> { ["hulks"] = "surge" });
        await _service.UpdatePlanetAsync("196", "automatons", null, new Dictionary<string, string>(), reset: true);
        Assert.Contains("planets --planet 196 --catalogue yes --reset yes", _fixture.ReadArgs());
        Assert.Empty((await _service.GetPlanetsAsync())!.Planets);

        await _service.UpdatePlanetAsync("Malevelon Creek", "automatons", ["jet-brigade"],
            new Dictionary<string, string> { ["hulks"] = "surge" });
        var cleared = await _service.ResetAllPlanetsAsync();
        Assert.Contains("planets --reset-all yes", _fixture.ReadArgs());
        Assert.Empty(cleared.Planets);
    }

    [Fact]
    public async Task Planets_WhenOldCliHasNoCommand_ShouldReturnNull()
    {
        Assert.Null(await _service.GetPlanetsAsync());
        Assert.DoesNotContain("planets ", _fixture.ReadArgs());
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