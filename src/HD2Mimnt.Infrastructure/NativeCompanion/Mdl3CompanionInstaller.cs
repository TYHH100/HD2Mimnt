using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD2Mimnt.Infrastructure.NativeCompanion;

public enum CompanionInstallState
{
    /// <summary>包内没有伴生载荷（无 BUILD.json 或源 DLL 缺失）。</summary>
    NoPayload,
    /// <summary>目标 DLL 与 config.json 尚未安装。</summary>
    NotInstalled,
    /// <summary>目标 DLL 哈希与 BUILD.json 一致，视为已安装。</summary>
    Installed,
    /// <summary>目标存在但哈希不符（被外部修改或版本不同）。</summary>
    Modified,
}

public sealed record CompanionStatus(CompanionInstallState State, CompanionBuild? Build);

public sealed record CompanionOperationResult(bool Success, string? Message = null);

/// <summary>
/// 复刻 MDL3 官方 install.ps1 -CompanionOnly 语义：把签名原生桥 DLL 安装到
/// <c>%LOCALAPPDATA%\MDL3</c>，合并保留 config.json 未知键，预创建游戏 VM 无法自建的用户目录，
/// 并写官方 schema 的 install-state.json 保证后续仍可运行官方脚本。哈希与签名校验失败一律拒绝安装。
/// </summary>
public sealed class Mdl3CompanionInstaller
{
    private const string ConfigFileName = "config.json";
    private const string StateFileName = "install-state.json";
    private const string GameProcessName = "helldivers2";

    /// <summary>config.json 中由管理器托管的键；其余键（未来版本新增配置）合并保留。</summary>
    private static readonly HashSet<string> ConfigManagedKeys = new(StringComparer.Ordinal)
    {
        "schema", "dll", "game_dir", "native_memory_mb", "task_jit", "example_autostart", "integration_selftest"
    };

    private const int ConfigSchema = 1;
    private const int NativeMemoryMb = 2048;
    private const string TaskJit = "on";
    private const string ProductName = "MDL3";

    /// <summary>MDL 2.2.1 起共享的用户目录（相对 %LOCALAPPDATA%）；游戏 VM 无法创建目录，须提前建好且永不删除。</summary>
    private static readonly string[] UserFolders =
    [
        "MDL/Helldivers2/Mods",
        "MDL/Helldivers2/Logs",
        "MDL/Helldivers2/Languages/Options",
        "CowboyBingus/Helldivers2/Logs",
        "HD2Runtime/mod_data",
    ];

    private readonly IAuthenticodeVerifier _verifier;
    private readonly string _localApplicationData;

    public Mdl3CompanionInstaller(IAuthenticodeVerifier? verifier = null, string? localApplicationData = null)
    {
        _verifier = verifier ?? new AuthenticodeVerifier();
        _localApplicationData = string.IsNullOrWhiteSpace(localApplicationData)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localApplicationData!;
    }

    public string DefaultDataRoot => Path.Combine(_localApplicationData, "MDL3");

    public CompanionStatus GetStatus(string modDirectory, string? dataRoot = null)
    {
        var build = CompanionBuildReader.Read(modDirectory);
        if (build == null) return new CompanionStatus(CompanionInstallState.NoPayload, null);
        dataRoot ??= DefaultDataRoot;
        var nativeTarget = ResolveNativeTarget(dataRoot, build.NativePath);
        if (!File.Exists(nativeTarget) || !File.Exists(Path.Combine(dataRoot, ConfigFileName)))
            return new CompanionStatus(CompanionInstallState.NotInstalled, build);
        var state = HashFile(nativeTarget);
        return new CompanionStatus(
            string.Equals(state, build.NativeSha256, StringComparison.OrdinalIgnoreCase)
                ? CompanionInstallState.Installed
                : CompanionInstallState.Modified,
            build);
    }

    /// <summary>
    /// 伴生组件是否已与当前包一致：原生桥哈希、config 托管键、install-state 与共享用户目录全部命中。
    /// 一致时 <see cref="Install"/> 整段跳过，避免每次部署重写配置并刷一遍进度。
    /// </summary>
    public bool IsCurrent(string modDirectory, string gamePath, string? dataRoot = null)
    {
        var build = CompanionBuildReader.Read(modDirectory);
        if (build == null) return false;
        dataRoot ??= DefaultDataRoot;
        var root = Path.GetFullPath(dataRoot);
        var nativeTarget = ResolveNativeTarget(root, build.NativePath);
        if (!File.Exists(nativeTarget) ||
            !string.Equals(HashFile(nativeTarget), build.NativeSha256, StringComparison.OrdinalIgnoreCase)) return false;
        // 游戏 VM 无法自建目录，被外部删除时必须回到安装流程重建。
        if (!Directory.Exists(Path.Combine(root, "Logs"))) return false;
        foreach (var folder in UserFolders)
        {
            if (!Directory.Exists(Path.Combine(_localApplicationData, folder.Replace('/', '\\')))) return false;
        }
        if (!ConfigIsCurrent(Path.Combine(root, ConfigFileName), nativeTarget, gamePath)) return false;
        return StateIsCurrent(Path.Combine(root, StateFileName), build, root, gamePath, nativeTarget);
    }

    /// <summary>安装/刷新伴生组件；幂等，已是最新时静默跳过且不产生任何进度或消息。</summary>
    public CompanionOperationResult Install(string modDirectory, string gamePath, string? dataRoot = null,
        Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsCurrent(modDirectory, gamePath, dataRoot)) return new CompanionOperationResult(true);
        var build = CompanionBuildReader.Read(modDirectory)
            ?? throw new InvalidOperationException("模组包没有可安装的原生伴生组件（缺少 BUILD.json 或源 DLL）");
        dataRoot ??= DefaultDataRoot;
        cancellationToken.ThrowIfCancellationRequested();

        var source = CompanionBuildReader.NativeSourcePath(modDirectory, build);
        var sourceHash = HashFile(source);
        if (!string.Equals(sourceHash, build.NativeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("源 DLL 与 BUILD.json 声明的 SHA256 不符，拒绝安装");

        var signature = _verifier.Verify(source);
        if (!signature.IsTrusted)
            throw new InvalidOperationException($"原生桥签名校验未通过：{signature.ErrorMessage ?? "未知原因"}");
        if (!string.Equals(signature.SignerThumbprint, build.NativeThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("原生桥签名证书指纹与 BUILD.json 不符，拒绝安装");

        EnsureNoGameRunning();
        progress?.Invoke("创建 MDL3 用户目录...");
        var root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(Path.Combine(root, "Logs"));
        foreach (var folder in UserFolders)
            Directory.CreateDirectory(Path.Combine(_localApplicationData, folder.Replace('/', '\\')));

        var nativeTarget = ResolveNativeTarget(root, build.NativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(nativeTarget)!);
        EnsureUnderRoot(nativeTarget, root);
        EnsureNoReparsePoint(nativeTarget);

        if (File.Exists(nativeTarget))
        {
            if (string.Equals(HashFile(nativeTarget), build.NativeSha256, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Invoke("原生桥已是目标版本，跳过复制。");
            }
            else
            {
                progress?.Invoke("备份旧的原生桥...");
                var backupDirectory = Path.Combine(root, "Backups", build.Version);
                Directory.CreateDirectory(backupDirectory);
                File.Copy(nativeTarget, Path.Combine(backupDirectory, Path.GetFileName(nativeTarget)), overwrite: true);
                File.Copy(source, nativeTarget, overwrite: true);
            }
        }
        else
        {
            File.Copy(source, nativeTarget, overwrite: false);
        }

        if (!string.Equals(HashFile(nativeTarget), build.NativeSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装后哈希校验失败，目标文件不完整");

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke("写入 MDL3 配置...");
        WriteConfig(root, nativeTarget, gamePath);
        WriteInstallState(root, build, gamePath, nativeTarget);
        return new CompanionOperationResult(true, $"MDL3 原生伴生组件已安装：{nativeTarget}");
    }

    /// <summary>移除管理器安装的原生桥与状态文件；config、日志与用户目录保留（对齐官方 Remove 语义）。</summary>
    public CompanionOperationResult Remove(string modDirectory, string? dataRoot = null,
        CancellationToken cancellationToken = default)
    {
        var build = CompanionBuildReader.Read(modDirectory);
        if (build == null) return new CompanionOperationResult(true);
        dataRoot ??= DefaultDataRoot;
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(dataRoot);
        var nativeTarget = ResolveNativeTarget(root, build.NativePath);

        if (File.Exists(nativeTarget))
        {
            EnsureNoGameRunning();
            if (!string.Equals(HashFile(nativeTarget), build.NativeSha256, StringComparison.OrdinalIgnoreCase))
                return new CompanionOperationResult(false, "原生桥已被外部修改，保留文件不予删除");
            File.Delete(nativeTarget);
        }

        var statePath = Path.Combine(root, StateFileName);
        if (File.Exists(statePath)) File.Delete(statePath);
        return new CompanionOperationResult(true, "MDL3 原生伴生组件已移除；配置与日志保留");
    }

    private string ResolveNativeTarget(string dataRoot, string nativePath)
        => Path.GetFullPath(Path.Combine(dataRoot, nativePath.Replace('/', '\\')));

    /// <summary>期望的托管键取值；写入与一致性判定共用，避免两处字面量漂移。</summary>
    private static JsonObject DesiredConfig(string nativeTarget, string gamePath) => new()
    {
        ["schema"] = ConfigSchema,
        ["dll"] = nativeTarget,
        ["game_dir"] = gamePath,
        ["native_memory_mb"] = NativeMemoryMb,
        ["task_jit"] = TaskJit,
        ["example_autostart"] = false,
        ["integration_selftest"] = false,
    };

    /// <summary>托管键逐项等于目标值即视为一致；未知键（未来版本的配置项）不参与判定。</summary>
    private static bool ConfigIsCurrent(string configPath, string nativeTarget, string gamePath)
    {
        JsonObject? config;
        try
        {
            config = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (config == null) return false;
        foreach (var (name, value) in DesiredConfig(nativeTarget, gamePath))
        {
            if (!config.TryGetPropertyValue(name, out var actual) || !JsonNode.DeepEquals(value, actual)) return false;
        }
        return true;
    }

    /// <summary>install-state.json 须仍指向当前包、游戏与原生桥，否则官方脚本会把已装组件当旧版本。</summary>
    private static bool StateIsCurrent(string statePath, CompanionBuild build, string root, string gamePath,
        string nativeTarget)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var state = document.RootElement;
            if (state.GetProperty("product").GetString() != ProductName ||
                state.GetProperty("version").GetString() != build.Version ||
                state.GetProperty("game").GetString() != gamePath ||
                state.GetProperty("data").GetString() != root) return false;
            var file = state.GetProperty("files").EnumerateArray().First();
            return file.GetProperty("path").GetString() == nativeTarget &&
                   string.Equals(file.GetProperty("sha256").GetString(), build.NativeSha256,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static void EnsureNoGameRunning()
    {
        if (Process.GetProcessesByName(GameProcessName).Length == 0) return;
        throw new InvalidOperationException("请先关闭 Helldivers 2 再安装或移除原生伴生组件");
    }

    private static void EnsureUnderRoot(string target, string root)
    {
        var prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标路径超出伴生组件安装根目录");
    }

    /// <summary>沿路径链检查 Reparse Point；符号链接/junction 中转一律拒绝，防止写入逃逸。</summary>
    private static void EnsureNoReparsePoint(string path)
    {
        var walk = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(walk) || File.Exists(walk))
            {
                var attributes = File.GetAttributes(walk);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException($"拒绝经过符号链接的路径: {walk}");
            }
            var parent = Path.GetDirectoryName(walk);
            if (parent == walk || string.IsNullOrEmpty(parent)) break;
            walk = parent;
        }
    }

    private void WriteConfig(string root, string nativeTarget, string gamePath)
    {
        var configPath = Path.Combine(root, ConfigFileName);
        // 官方脚本语义：只接管托管键，其余键（未来版本新增配置）合并保留；旧文件先备份。
        var config = new JsonObject();
        if (File.Exists(configPath))
        {
            try
            {
                var old = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
                if (old != null)
                    foreach (var (name, value) in old)
                        if (!ConfigManagedKeys.Contains(name))
                            config[name] = value?.DeepClone();
                File.Copy(configPath, configPath + ".backup", overwrite: true);
            }
            catch (JsonException)
            {
                // 损坏的旧配置不保留未知键，直接按默认重建。
            }
        }

        foreach (var (name, value) in DesiredConfig(nativeTarget, gamePath))
            config[name] = value?.DeepClone();
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WriteInstallState(string root, CompanionBuild build, string gamePath, string nativeTarget)
    {
        var statePath = Path.Combine(root, StateFileName);
        var payload = new Dictionary<string, object?>
        {
            ["product"] = ProductName,
            ["version"] = build.Version,
            ["game"] = gamePath,
            ["data"] = root,
            ["files"] = new[] { new Dictionary<string, string> { ["path"] = nativeTarget, ["sha256"] = build.NativeSha256.ToLowerInvariant() } },
            ["installed_utc"] = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(statePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
