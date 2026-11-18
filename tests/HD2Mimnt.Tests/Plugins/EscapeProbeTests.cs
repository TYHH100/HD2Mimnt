using HD2Mimnt.PluginSDK;
using HD2Mimnt.Samples.EscapeProbe;
using Xunit;

namespace HD2Mimnt.Tests.Plugins;

/// <summary>
/// 沙箱逃逸探针：以 0 权限 + Sandbox 模式的插件身份直接调用 .NET，实证进程边界的强度。
///
/// 这些用例断言的是**当前已确认的设计现状**：插件与主程序同用户同权限，
/// Sandbox 模式与权限清单约束的是 SDK 调用，而不是进程能力。
/// 一旦引入 OS 级隔离（受限令牌 / AppContainer），这里的断言应当反转。
/// </summary>
public class EscapeProbeTests
{
    [Fact]
    public async Task Plugin_DeclaresSandboxModeWithoutAnyPermission()
    {
        var manifest = new EscapeProbePlugin().Manifest;

        Assert.Equal(PluginPermissionMode.Sandbox, manifest.PermissionMode);
        Assert.Empty(manifest.RequestedPermissions);
        Assert.Equal("EscapeProbe.dll", manifest.Assembly);
    }

    [Fact]
    public async Task FileWrite_ReachesSystemTempOutsidePluginStorage()
    {
        var result = await new EscapeFileWriteCommand().ExecuteAsync(Context());

        Assert.True(result.Success, result.Error);
        Assert.Equal("escape-file-write:escaped", result.Message);
    }

    [Fact]
    public async Task FileRead_ReadsFilesOutsidePluginStorage()
    {
        var result = await new EscapeFileReadCommand().ExecuteAsync(Context());

        Assert.True(result.Success, result.Error);
        Assert.Equal("escape-file-read:escaped", result.Message);
    }

    [Fact]
    public async Task Process_StartsChildProcess()
    {
        var result = await new EscapeProcessCommand().ExecuteAsync(Context());

        Assert.True(result.Success, result.Error);
        Assert.Equal("escape-process:escaped", result.Message);
    }

    [Fact]
    public async Task Environment_EnumeratesProcessVariables()
    {
        var result = await new EscapeEnvironmentCommand().ExecuteAsync(Context());

        Assert.True(result.Success, result.Error);
        Assert.Equal("escape-env:escaped", result.Message);
    }

    /// <summary>
    /// 外联结果依赖运行环境（CI 可能禁网），因此只断言“给出了明确判定”，不预设成败。
    /// </summary>
    [Fact]
    public async Task Network_ReturnsDefinitiveVerdict()
    {
        var result = await new EscapeNetworkCommand().ExecuteAsync(Context());

        Assert.True(result.Success, result.Error);
        Assert.True(
            result.Message is "escape-network:escaped" or "escape-network:contained",
            $"未预期的判定：{result.Message}");
        Assert.NotNull(result.Ui);
    }

    [Fact]
    public async Task Report_RunsAllFiveVectors()
    {
        var result = await new EscapeReportCommand().ExecuteAsync(Context());

        Assert.True(result.Success, result.Error);
        Assert.StartsWith("escape-report:", result.Message);
        Assert.EndsWith("/5", result.Message);
        Assert.NotNull(result.Ui);

        var table = result.Ui!.Blocks[0];
        Assert.Equal(PluginUiBlockTypes.Markdown, table.Type);
        Assert.Contains("进程隔离实测", table.Text);
    }

    private static PluginCommandContext Context()
        => new(new SilentLogger(), new ScratchStorage(), new ScratchConfiguration());

    private sealed class SilentLogger : IPluginLogger
    {
        public List<string> Messages { get; } = new();

        public void Info(string message) => Messages.Add("info:" + message);

        public void Warning(string message) => Messages.Add("warn:" + message);

        public void Error(string message, Exception? exception = null) => Messages.Add("error:" + message);

        public void Debug(string message) => Messages.Add("debug:" + message);
    }

    private sealed class ScratchStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _files = new();

        public string GetDataDirectory() => Path.GetTempPath();

        public Task<string> ReadFileAsync(string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult(_files.TryGetValue(fileName, out var content) ? content : string.Empty);

        public Task WriteFileAsync(string fileName, string content, CancellationToken cancellationToken = default)
        {
            _files[fileName] = content;
            return Task.CompletedTask;
        }

        public bool FileExists(string fileName) => _files.ContainsKey(fileName);

        public Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default)
        {
            _files.Remove(fileName);
            return Task.CompletedTask;
        }
    }

    private sealed class ScratchConfiguration : IPluginConfiguration
    {
        public string? Get(string key) => null;

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IEnumerable<string> GetKeys() => Array.Empty<string>();

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
