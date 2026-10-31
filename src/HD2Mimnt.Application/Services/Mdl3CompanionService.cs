using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.NativeCompanion;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// MDL3 原生伴生组件（%LOCALAPPDATA%\MDL3 的签名原生桥、config.json 与用户目录）的安装门面。
/// 只接管 MDL3 加载器角色的模组；伴生载荷来自包内 BUILD.json，不执行官方 install.ps1。
/// </summary>
public class Mdl3CompanionService
{
    private readonly Mdl3CompanionInstaller _installer;
    private readonly ILogger<Mdl3CompanionService>? _logger;

    public Mdl3CompanionService(IAuthenticodeVerifier? verifier = null,
        ILogger<Mdl3CompanionService>? logger = null, string? localApplicationData = null)
    {
        _installer = new Mdl3CompanionInstaller(verifier, localApplicationData);
        _logger = logger;
    }

    /// <summary>包内是否存在可安装的伴生载荷（BUILD.json + 源 DLL）；MDL 2.x 等旧包返回 false。</summary>
    public bool HasCompanionPayload(Mod mod) => CompanionBuildReader.Read(mod.Path) != null;

    public CompanionStatus GetStatus(Mod mod) => _installer.GetStatus(mod.Path);

    /// <summary>确保伴生组件已安装；幂等。游戏运行中或签名/哈希校验失败时抛出异常。</summary>
    public Task<CompanionOperationResult> EnsureInstalledAsync(Mod mod, string gamePath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _installer.Install(mod.Path, gamePath, progress: message => progress?.Report(message),
            cancellationToken: cancellationToken);
        return Task.FromResult(result);
    }

    /// <summary>移除伴生组件；文件被外部修改时保留并返回失败结果，不抛异常。</summary>
    public Task<CompanionOperationResult> RemoveAsync(Mod mod,
        CancellationToken cancellationToken = default)
    {
        var result = _installer.Remove(mod.Path, cancellationToken: cancellationToken);
        if (!result.Success) _logger?.LogWarning("MDL3 伴生组件移除被拒绝: {Message}", result.Message);
        return Task.FromResult(result);
    }
}
