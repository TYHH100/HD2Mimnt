using HD2Mimnt.Application.Cli;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HD2Mimnt.Tests.Cli;

/// <summary>Catalog 元数据与确认守卫测试：不触碰真实服务，空容器即可。</summary>
public class CliCommandCatalogTests
{
    private static readonly ServiceProvider Empty = new ServiceCollection().BuildServiceProvider();

    [Fact]
    public async Task UnknownCommand_ReturnsUsageWithHelp()
    {
        var response = await CliCommandCatalog.Build().DispatchAsync(
            new[] { "mod", "frobnicate" }, Empty, json: false, yes: true, CancellationToken.None);
        Assert.Equal(2, response.ExitCode);
        Assert.Contains("未知命令", response.Message);
        Assert.Contains("mod list", response.Message);
    }

    [Fact]
    public async Task DestructiveCommands_RequireYes()
    {
        var catalog = CliCommandCatalog.Build();
        string[][] destructive =
        {
            new[] { "mod", "delete", "00000000-0000-0000-0000-000000000001" },
            new[] { "profile", "create", "新配置" },
            new[] { "profile", "activate", "默认" },
            new[] { "deploy" },
            new[] { "undeploy" },
            new[] { "compat", "repair", "某个模组" },
            new[] { "settings", "set", "Game.Path", @"C:\Temp" },
        };
        foreach (var tokens in destructive)
        {
            var response = await catalog.DispatchAsync(tokens, Empty, json: false, yes: false, CancellationToken.None);
            Assert.True(response.RequiresYes, $"{string.Join(' ', tokens)} 应要求确认");
            Assert.Equal(2, response.ExitCode);
            Assert.Contains("--yes", response.Message);
        }
    }

    [Fact]
    public async Task DeployStatus_IsNotTreatedAsDeploy()
    {
        // 回归：单词命中必须让位于两段式，"deploy status" 不能被宿主命令抢先并要求 --yes。
        var response = await CliCommandCatalog.Build().DispatchAsync(
            new[] { "deploy", "status" }, Empty, json: false, yes: false, CancellationToken.None);
        Assert.False(response.RequiresYes);
    }

    [Fact]
    public void HelpText_ListsAllCommandGroups()
    {
        var help = CliCommandCatalog.Build().BuildHelpText();
        foreach (var group in new[] { "status", "mod ", "profile ", "deploy", "undeploy", "compat ", "settings ", "loader ", "ias " })
            Assert.Contains(group, help);
    }
}

/// <summary>命令 handler 测试：临时 SQLite + 真实服务（不含 UI），覆盖只读与库内写操作。</summary>
public class CliCommandHandlerTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly ServiceProvider _services;
    private readonly string _testDbPath;
    private readonly string _storageDirectory;
    private readonly string _appDataDirectory;

    public CliCommandHandlerTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_cli_{Guid.NewGuid()}.db");
        _storageDirectory = Path.Combine(Path.GetTempPath(), $"test_cli_mods_{Guid.NewGuid()}");
        _appDataDirectory = Path.Combine(Path.GetTempPath(), $"test_cli_appdata_{Guid.NewGuid()}");
        Directory.CreateDirectory(_storageDirectory);
        Directory.CreateDirectory(_appDataDirectory);

        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        var settingsService = new SettingsService(new SettingsRepository(_testDbPath), _appDataDirectory);
        var profileService = new ProfileService(new ProfileRepository(_dbContext), new ModRepository(_dbContext), settingsService);
        var deploymentService = new DeploymentService(
            new ProfileRepository(_dbContext), new ModRepository(_dbContext),
            new DeploymentRepository(_dbContext), new List<HD2Mimnt.Core.Interfaces.IDeploymentMethod> { new CopyDeploymentMethod() });

        _services = new ServiceCollection()
            .AddSingleton(_dbContext)
            .AddSingleton<HD2Mimnt.Core.Interfaces.IModRepository>(new ModRepository(_dbContext))
            .AddSingleton<HD2Mimnt.Core.Interfaces.IProfileRepository>(new ProfileRepository(_dbContext))
            .AddSingleton(settingsService)
            .AddSingleton(profileService)
            .AddSingleton(new ModService(new ModRepository(_dbContext), _storageDirectory))
            .AddSingleton(deploymentService)
            .AddSingleton(new ModCompatibilityService(new ModRepository(_dbContext),
                new ModCompatibilityAnalyzer(), new ModService(new ModRepository(_dbContext), _storageDirectory)))
            .BuildServiceProvider();

        profileService.InitializeAsync().Wait();
    }

    private Task<CliCommandResponse> Dispatch(params string[] tokens)
        => CliCommandCatalog.Build().DispatchAsync(tokens, _services, json: false, yes: true, CancellationToken.None);

    private async Task<Mod> CreateModAsync(string name)
    {
        var repository = _services.GetRequiredService<HD2Mimnt.Core.Interfaces.IModRepository>();
        var mod = new Mod
        {
            Id = Guid.NewGuid(), Name = name, Version = "1.0", Path = Path.Combine(_storageDirectory, name),
            Source = ModSource.LocalZip, Status = ModStatus.Ready, ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow
        };
        await repository.AddAsync(mod);
        return mod;
    }

    [Fact]
    public async Task ModList_ShouldReturnImportedMods()
    {
        var mod = await CreateModAsync("测试模组A");
        var response = await Dispatch("mod", "list");
        Assert.Equal(0, response.ExitCode);
        var json = System.Text.Json.JsonSerializer.Serialize(response.Data,
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        Assert.Contains(mod.Id.ToString(), json);
        Assert.Contains("测试模组A", json);
    }

    [Fact]
    public async Task ModShow_SupportsIdAndUniqueName()
    {
        var mod = await CreateModAsync("独特名字模组");
        var byName = await Dispatch("mod", "show", "独特名字模组");
        Assert.Equal(0, byName.ExitCode);
        var byId = await Dispatch("mod", "show", mod.Id.ToString());
        Assert.Equal(0, byId.ExitCode);
        var missing = await Dispatch("mod", "show", "不存在的模组");
        Assert.Equal(1, missing.ExitCode);
    }

    [Fact]
    public async Task ModEnableDisable_ManagesProfileMembership()
    {
        var mod = await CreateModAsync("启停测试模组");
        var profile = (await _services.GetRequiredService<ProfileService>().GetAllProfilesAsync()).First();

        var enable = await Dispatch("mod", "enable", mod.Id.ToString());
        Assert.Equal(0, enable.ExitCode);

        var refreshed = (await _services.GetRequiredService<ProfileService>().GetProfileByIdAsync(profile.Id))!;
        var member = Assert.Single(refreshed.Mods);
        Assert.Equal(mod.Id, member.ModId);
        Assert.True(member.Enabled);

        var disable = await Dispatch("mod", "disable", mod.Id.ToString());
        Assert.Equal(0, disable.ExitCode);
        refreshed = (await _services.GetRequiredService<ProfileService>().GetProfileByIdAsync(profile.Id))!;
        Assert.False(refreshed.Mods.Single().Enabled);
    }

    [Fact]
    public async Task ProfileCreateActivateAndFlow_WorksThroughCli()
    {
        var create = await Dispatch("profile", "create", "CLI测试配置");
        Assert.Equal(0, create.ExitCode);

        var activate = await Dispatch("profile", "activate", "CLI测试配置");
        Assert.Equal(0, activate.ExitCode);
        var active = await _services.GetRequiredService<ProfileService>().GetActiveProfileAsync();
        Assert.Equal("CLI测试配置", active!.Name);

        var mod = await CreateModAsync("配置成员模组");
        var add = await Dispatch("profile", "add-mod", "CLI测试配置", "配置成员模组");
        Assert.Equal(0, add.ExitCode);
        var remove = await Dispatch("profile", "remove-mod", "CLI测试配置", "配置成员模组");
        Assert.Equal(0, remove.ExitCode);
        var refreshed = (await _services.GetRequiredService<ProfileService>().GetProfileByIdAsync(active.Id))!;
        Assert.DoesNotContain(refreshed.Mods, item => item.ModId == mod.Id);
    }

    [Fact]
    public async Task SettingsWhitelist_RejectsUnknownKeyAndMissingDirectory()
    {
        var unknown = await Dispatch("settings", "set", "Some.Unknown.Key", "1");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("白名单", unknown.Message);

        var badPath = await Dispatch("settings", "set", "Game.Path", @"Z:\不存在的目录\hd2");
        Assert.Equal(1, badPath.ExitCode);

        var goodPath = await Dispatch("settings", "set", "Game.Path", _appDataDirectory);
        Assert.Equal(0, goodPath.ExitCode);
        var read = await Dispatch("settings", "get", "Game.Path");
        Assert.Equal(0, read.ExitCode);
        Assert.Contains(_appDataDirectory, read.Message);
    }

    [Fact]
    public async Task ModImport_MissingPathFailsCleanly()
    {
        var response = await Dispatch("mod", "import", @"Z:\不存在的包.zip");
        Assert.Equal(1, response.ExitCode);
        Assert.Contains("路径不存在", response.Message);
    }

    public void Dispose()
    {
        _services.Dispose();
        _dbContext.Dispose();
        if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, true);
        if (Directory.Exists(_appDataDirectory)) Directory.Delete(_appDataDirectory, true);
    }
}
