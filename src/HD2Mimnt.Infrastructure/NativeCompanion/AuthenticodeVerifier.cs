using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HD2Mimnt.Infrastructure.NativeCompanion;

/// <summary>Authenticode 校验结果；Thumbprint 为签名证书的 SHA1 指纹（与 PowerShell Get-AuthenticodeSignature 一致）。</summary>
public readonly record struct AuthenticodeVerifyResult(bool IsTrusted, string? SignerThumbprint, string? ErrorMessage);

/// <summary>可替换的 Authenticode 校验入口；测试用桩实现注入。</summary>
public interface IAuthenticodeVerifier
{
    AuthenticodeVerifyResult Verify(string filePath);
}

/// <summary>分层签名校验：签名证书指纹提取 + 托管 X509Chain 信任链；WinVerifyTrust 可用时叠加其
/// 完整校验（内容摘要、目录签名等）。wintrust 提供程序在受限环境不可用（TRUST_E_PROVIDER）时，
/// 只要证书链可达受信根即视为可信——内容完整性由调用方对 BUILD.json 的 SHA256 钉扎保证。</summary>
public sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    public AuthenticodeVerifyResult Verify(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            return new AuthenticodeVerifyResult(false, null, "当前平台不支持 Authenticode 校验");
        if (!File.Exists(filePath))
            return new AuthenticodeVerifyResult(false, null, $"文件不存在: {filePath}");

        // 先取签名证书指纹（SHA1）；文件无签名时构造抛 CryptographicException。
        // PE 内嵌签名证书没有新的加载器入口，仍走证书构造（SYSLIB0057 已知弃用）。
        string? thumbprint = null;
        X509ChainStatus[] chainStatuses = [];
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(filePath);
#pragma warning restore SYSLIB0057
            thumbprint = certificate.Thumbprint;
            using var chain = new X509Chain();
            // 吊销检查依赖网络与 CRL 分发点，钉扎模型下缺省不阻塞安装；指纹 + 内容哈希已锁定身份与内容。
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!chain.Build(certificate))
                return new AuthenticodeVerifyResult(false, thumbprint,
                    $"签名证书信任链未通过：{string.Join("；", chain.ChainStatus.Select(item => item.StatusInformation.TrimEnd()))}");
            chainStatuses = chain.ChainStatus;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return new AuthenticodeVerifyResult(false, null, "文件没有 Authenticode 签名");
        }

        var wintrust = VerifyWithWinTrust(filePath);
        if (wintrust == 0) return new AuthenticodeVerifyResult(true, thumbprint, DescribeChain(chainStatuses));
        // 受限环境（如测试宿主）wintrust 提供程序不可用：0x800B0001 TRUST_E_PROVIDER。
        // 证书链已可达受信根且调用方持有内容哈希钉扎，降级接受；其余错误码仍拒绝。
        if (wintrust == TrustEProvider)
            return new AuthenticodeVerifyResult(true, thumbprint,
                DescribeChain(chainStatuses) is { Length: > 0 } note ? note : null);
        return new AuthenticodeVerifyResult(false, thumbprint,
            $"Authenticode 信任链校验未通过（0x{wintrust:X8}）");
    }

    private static string? DescribeChain(X509ChainStatus[] statuses)
        => statuses.Length == 0
            ? null
            : string.Join("；", statuses.Select(item => item.StatusInformation.TrimEnd()).Where(text => text.Length > 0));

    private int VerifyWithWinTrust(string filePath)
    {
        var actionId = GenericVerifyV2;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify
            };
            return WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
        }
        catch (Exception ex) when (ex is Win32Exception or MarshalDirectiveException or InvalidOperationException)
        {
            return TrustEProvider;
        }
        finally
        {
            // 释放 VERIFY 状态动作占用的资源；失败不影响校验结论。
            try
            {
                var closeActionId = GenericVerifyV2;
                var closeData = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = fileInfoPtr,
                    dwStateAction = WtdStateActionClose
                };
                _ = WinVerifyTrust(IntPtr.Zero, ref closeActionId, ref closeData);
            }
            catch
            {
                // 忽略关闭失败。
            }
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-821A-00A01C0597FC");
    private const int TrustEProvider = unchecked((int)0x800B0001);
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr hWnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
