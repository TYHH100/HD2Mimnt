using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HD2Mimnt.Infrastructure.NativeCompanion;

/// <summary>证书存储的查询与写入接缝；测试注入内存桩。</summary>
public interface IEacCertificateStore
{
    /// <summary>目标证书（按指纹）已在任一存储中时返回 true。</summary>
    bool Contains(X509Certificate2 certificate);

    /// <summary>把证书写入目标存储。</summary>
    void Add(X509Certificate2 certificate);
}

/// <summary>MDL3 官方 EAC Fix Kit 语义的证书安装器：从模组包内的 MDL3-EAC-Fix-Kit 压缩包中取出
/// 签名链中间 CA 证书（Certum Code Signing 2021 CA），先按指纹检测是否已安装（含 LocalMachine，
/// 官方脚本 certutil -addstore CA 装的位置），已装则静默跳过，缺失才写入当前用户中间 CA 存储
/// ——无需管理员权限，且同用户运行的游戏构建签名信任链时同样生效。找不到证书源时静默返回。</summary>
public sealed class EacCertificateInstaller
{
    /// <summary>官方 Fix Kit 的中间 CA 主题片段；也是从包内 zip 提取证书时的准入守卫。</summary>
    public const string ExpectedSubjectFragment = "Certum Code Signing 2021 CA";

    /// <summary>官方脚本 certutil -verifystore 校验用的 SHA1 指纹，仅用于文档对照；运行时以包内 .cer 实测指纹为准。</summary>
    public const string OfficialThumbprint = "8D57E2B4008BBE461470A69F3492BC5AE362A7A9";

    private const long MaxCertificateBytes = 64 * 1024;

    private readonly IEacCertificateStore _store;

    public EacCertificateInstaller(IEacCertificateStore? store = null)
        => _store = store ?? new WindowsCertificateAuthorityStore();

    /// <summary>
    /// 确保 mod 包附带的 EAC 信任证书已安装；幂等：无证书源、证书已在任一中间 CA 存储时静默返回（无进度无消息），
    /// 只有实际写入时才回报进度并携带消息。存储写入失败向上抛出，由调用方决定是否阻断伴生安装。
    /// </summary>
    public CompanionOperationResult EnsureInstalled(string modDirectory, Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return new CompanionOperationResult(true);
        var certificate = FindKitCertificate(modDirectory);
        if (certificate == null) return new CompanionOperationResult(true);
        cancellationToken.ThrowIfCancellationRequested();
        // 已装过（含官方脚本装进 LocalMachine）就不再安装，也不产生任何进度与消息。
        if (_store.Contains(certificate)) return new CompanionOperationResult(true);
        progress?.Invoke($"安装 EAC 信任证书（{ExpectedSubjectFragment}）...");
        _store.Add(certificate);
        return new CompanionOperationResult(true, $"已安装 EAC 信任证书：{certificate.Subject}");
    }

    /// <summary>在 mod 目录顶层的 zip 中定位 Fix Kit 内的中间 CA 证书；找不到或内容无效时返回 null。</summary>
    internal static X509Certificate2? FindKitCertificate(string modDirectory)
    {
        if (!Directory.Exists(modDirectory)) return null;
        foreach (var zipPath in Directory.EnumerateFiles(modDirectory, "*.zip", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".cer", StringComparison.OrdinalIgnoreCase) ||
                        entry.Length is 0 or > MaxCertificateBytes) continue;
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    X509Certificate2? certificate;
                    try
                    {
                        certificate = X509CertificateLoader.LoadCertificate(buffer.ToArray());
                    }
                    catch (CryptographicException)
                    {
                        continue;
                    }
                    // 主题守卫：只认官方 Fix Kit 的 Certum 中间 CA，包内其余 .cer 一律不导入。
                    if (certificate.Subject.Contains(ExpectedSubjectFragment, StringComparison.OrdinalIgnoreCase))
                        return certificate;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // 损坏或不可读的 zip 不是证书来源，跳过继续找下一个。
            }
        }
        return null;
    }
}

/// <summary>默认实现：检测遍历 CurrentUser/LocalMachine 的中间 CA 存储（官方脚本装 LocalMachine），
/// 写入只落 CurrentUser（无需管理员权限）。</summary>
public sealed class WindowsCertificateAuthorityStore : IEacCertificateStore
{
    public bool Contains(X509Certificate2 certificate)
    {
        foreach (var location in (StoreLocation[]) [StoreLocation.CurrentUser, StoreLocation.LocalMachine])
        {
            try
            {
                using var store = new X509Store(StoreName.CertificateAuthority, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint,
                        validOnly: false).Count > 0)
                    return true;
            }
            catch (CryptographicException)
            {
                // 存储不可读（如受限环境）视为未安装，交给写入路径兜底。
            }
        }
        return false;
    }

    public void Add(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }
}
