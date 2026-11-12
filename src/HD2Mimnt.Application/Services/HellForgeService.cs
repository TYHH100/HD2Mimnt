using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 通过 HellForge 官方 CLI（app\cli\HellForge.Cli.exe）的 JSON 接口读取安装状态、管理玩家档案。
/// 只调用 CLI，不直接触碰 game\、app\ 或本地后端；会话检查仍由 HellForge 自己的会话运行器完成。
/// 接口约定见 HellForge 仓库 docs/external-launchers.md（GPL-3.0，独立进程调用，不复制其代码）。
/// </summary>
public class HellForgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(30);

    private readonly SettingsService _settingsService;
    private readonly ILogger<HellForgeService>? _logger;

    public HellForgeService(SettingsService settingsService, ILogger<HellForgeService>? logger = null)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>设置中的 HellForge 目录能否解析出安装根目录（含 hellforge-install.json 标记）。</summary>
    public bool TryGetInstallationRoot(out string root)
    {
        var configured = _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty);
        return GameInstallationPaths.TryResolveHellForgeRoot(configured, out root);
    }

    /// <summary>安装根目录下的官方 CLI 路径；未配置或文件不存在时返回 false。</summary>
    public bool TryGetCliPath(out string cli)
    {
        cli = string.Empty;
        if (!TryGetInstallationRoot(out var root)) return false;
        cli = Path.Combine(root, "app", "cli", "HellForge.Cli.exe");
        return File.Exists(cli);
    }

    public Task<HellForgeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeStatus>(["status"], cancellationToken);

    public Task<HellForgeProfiles> GetProfilesAsync(CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeProfiles>(["profiles"], cancellationToken);

    public Task<HellForgeProfiles> CreateProfileAsync(string name, string template, CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeProfiles>(["profiles", "--create", name, "--template", template], cancellationToken);

    /// <summary>选择档案（settings --profile），对下一次会话生效；输出为保存后的完整设置。</summary>
    public Task SelectProfileAsync(string name, CancellationToken cancellationToken = default)
        => RunJsonAsync<JsonElement>(["settings", "--profile", name], cancellationToken);

    /// <summary>停止当前会话（stop --wait yes）：先请游戏自行关闭，再等待清理完成；比常规命令给更长的超时。</summary>
    public Task StopSessionAsync(CancellationToken cancellationToken = default)
        => RunJsonAsync<JsonElement>(["stop", "--wait", "yes"], cancellationToken, TimeSpan.FromSeconds(90));

    /// <summary>Mods 页列表（顶部在前）+ 问题 + modlist code。会话运行中也可读取。</summary>
    public Task<HellForgeModsList> GetModsAsync(CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeModsList>(["mods"], cancellationToken);

    /// <summary>添加模组（zip/rar/7z、文件夹模组的 hellforge-mod.json 或外观包 manifest.json）；同 id 替换。
    /// 复制大模组可能耗时数分钟，给足超时；会话运行中 CLI 会拒绝。</summary>
    public Task<HellForgeModAddResult> AddModAsync(string file, CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeModAddResult>(["mod-add", "--file", file], cancellationToken, TimeSpan.FromMinutes(10));

    /// <summary>删除模组（bundled 会自愈，应改用关闭）；会话运行中 CLI 会拒绝。</summary>
    public Task<HellForgeModRemoveResult> RemoveModAsync(string id, CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeModRemoveResult>(["mod-remove", "--id", id], cancellationToken);

    /// <summary>开关模组；会话运行中 CLI 会拒绝。</summary>
    public Task<HellForgeModSwitchResult> SwitchModAsync(string id, bool on, CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeModSwitchResult>(["mod-switch", "--id", id, "--on", on ? "yes" : "no"], cancellationToken);

    /// <summary>把所列模组置顶（top-first，顶部最后加载）；其余模组保持原顺序跟在后面，gameplay 仍在 cosmetic 之上。</summary>
    public Task<HellForgeModOrderResult> OrderModsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeModOrderResult>(["mod-order", "--ids", string.Join(",", ids)], cancellationToken);

    /// <summary>设置外观包的一个选项：on 开关、variant 选子选项（二者至少给一个）。</summary>
    public Task<HellForgeModPackResult> SetPackOptionAsync(string id, string option, bool? on = null,
        string? variant = null, CancellationToken cancellationToken = default)
    {
        var command = new List<string> { "mod-pack", "--id", id, "--option", option };
        if (on.HasValue) { command.Add("--on"); command.Add(on.Value ? "yes" : "no"); }
        if (variant != null) { command.Add("--variant"); command.Add(variant); }
        return RunJsonAsync<HellForgeModPackResult>(command.ToArray(), cancellationToken);
    }

    /// <summary>读取启动器设置（settings）：含档案、war、沙箱开关、陌生人联机、forge 掉落率等。</summary>
    public Task<HellForgeLauncherSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => RunJsonAsync<HellForgeLauncherSettings>(["settings"], cancellationToken);

    /// <summary>写入设置（settings --key value ...），返回保存后的完整设置。键不带 --；会话运行中 CLI 会拒绝改会话相关的项。</summary>
    public Task<HellForgeLauncherSettings> UpdateSettingsAsync(IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken = default)
    {
        var command = new List<string> { "settings" };
        foreach (var (key, value) in options)
        {
            command.Add("--" + key);
            command.Add(value);
        }
        return RunJsonAsync<HellForgeLauncherSettings>(command.ToArray(), cancellationToken);
    }

    /// <summary>会话日志列表（logs）：运行目录，最新在前；可选地列出某个运行目录下的文件。</summary>
    public Task<HellForgeLogsResult> GetLogsAsync(string? runId = null, CancellationToken cancellationToken = default)
    {
        var command = new List<string> { "logs" };
        if (runId != null) { command.Add("--run"); command.Add(runId); }
        return RunJsonAsync<HellForgeLogsResult>(command.ToArray(), cancellationToken);
    }

    /// <summary>把某次会话的日志打包成 zip（logs --export）；CLI 拒绝覆盖已存在的文件，故先删除目标。</summary>
    public Task<HellForgeLogExportResult> ExportLogsAsync(string zipPath, string? runId = null,
        CancellationToken cancellationToken = default)
    {
        PrepareExportTarget(zipPath);
        var command = new List<string> { "logs" };
        if (runId != null) { command.Add("--run"); command.Add(runId); }
        command.Add("--export");
        command.Add(zipPath);
        return RunJsonAsync<HellForgeLogExportResult>(command.ToArray(), cancellationToken, TimeSpan.FromMinutes(5));
    }

    /// <summary>生成崩溃报告 zip（logs --crash-report）；dump 为 true 时含崩溃转储。CLI 拒绝覆盖，故先删除目标。</summary>
    public Task<HellForgeCrashReportResult> CreateCrashReportAsync(string zipPath, bool dump = false, string? runId = null,
        CancellationToken cancellationToken = default)
    {
        PrepareExportTarget(zipPath);
        var command = new List<string> { "logs" };
        if (runId != null) { command.Add("--run"); command.Add(runId); }
        command.Add("--crash-report");
        command.Add(zipPath);
        if (dump) { command.Add("--dump"); command.Add("yes"); }
        return RunJsonAsync<HellForgeCrashReportResult>(command.ToArray(), cancellationToken, TimeSpan.FromMinutes(10));
    }

    private static void PrepareExportTarget(string zipPath)
    {
        var full = Path.GetFullPath(zipPath);
        var folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        if (File.Exists(full)) File.Delete(full);
    }

    /// <summary>构造调用官方 CLI 的进程信息（含 --install）；未配置或 CLI 缺失时抛出。</summary>
    public ProcessStartInfo CreateCliStartInfo(params string[] command)
    {
        if (!TryGetInstallationRoot(out var root))
            throw new InvalidOperationException("尚未配置有效的 HellForge 安装目录。");
        var cli = Path.Combine(root, "app", "cli", "HellForge.Cli.exe");
        if (!File.Exists(cli))
            throw new InvalidOperationException($"未找到 HellForge CLI：{cli}");

        var startInfo = new ProcessStartInfo(cli)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in command) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--install");
        startInfo.ArgumentList.Add(root);
        return startInfo;
    }

    private async Task<T> RunJsonAsync<T>(string[] command, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var startInfo = CreateCliStartInfo(command);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 HellForge CLI 进程。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? CliTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Kill(entireProcessTree) 在子进程已退出时抛 AggregateException（包裹 Win32Exception），须一并吞掉。
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(outputTask, errorTask); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new InvalidOperationException($"HellForge CLI 命令 {command[0]} 超时（{(timeout ?? CliTimeout).TotalSeconds:0} 秒）。");
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            _logger?.LogWarning("HellForge CLI {Command} 失败（退出码 {ExitCode}）: {Detail}", command[0], process.ExitCode, detail);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"HellForge CLI 退出码 {process.ExitCode}。"
                : detail);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(output, JsonOptions)
                ?? throw new InvalidOperationException("HellForge CLI 返回了空结果。");
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "HellForge CLI {Command} 输出无法解析", command[0]);
            throw new InvalidOperationException("HellForge CLI 输出无法解析，可能版本过旧。", ex);
        }
    }
}