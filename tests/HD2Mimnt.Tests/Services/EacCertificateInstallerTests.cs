using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HD2Mimnt.Infrastructure.NativeCompanion;

namespace HD2Mimnt.Tests.Services;

/// <summary>EAC 信任证书安装器：Fix Kit zip 证书提取、主题守卫、已装检测跳过与写入语义。</summary>
public sealed class EacCertificateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-EacCert-{Guid.NewGuid():N}");
    private readonly FakeCertificateStore _store = new();

    public EacCertificateInstallerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PackageWithoutKitZip_ShouldStaySilent()
    {
        var installer = new EacCertificateInstaller(_store);
        var progress = new CollectingProgress();

        var result = installer.EnsureInstalled(_root, progress.Report);

        Assert.True(result.Success);
        Assert.Null(result.Message);
        Assert.Empty(progress.Messages);
        Assert.Empty(_store.Certificates);
    }

    [Fact]
    public void CertificateAlreadyInstalled_ShouldSkipImportSilently()
    {
        using var certificate = CreateCert($"CN={EacCertificateInstaller.ExpectedSubjectFragment}");
        WriteKitZip("MDL3-EAC-Fix-Kit-1.0.2.zip", certificate);
        _store.Certificates.Add(certificate);
        var installer = new EacCertificateInstaller(_store);
        var progress = new CollectingProgress();

        var result = installer.EnsureInstalled(_root, progress.Report);

        Assert.True(result.Success);
        // 已装过就不再安装：无消息、无进度、无写入。
        Assert.Null(result.Message);
        Assert.Empty(progress.Messages);
        Assert.Single(_store.Certificates);
    }

    [Fact]
    public void MissingCertificate_ShouldImportAndReport()
    {
        using var certificate = CreateCert(
            $"CN={EacCertificateInstaller.ExpectedSubjectFragment}, O=Asseco Data Systems S.A., C=PL");
        WriteKitZip("MDL3-EAC-Fix-Kit-1.0.2.zip", certificate);
        var installer = new EacCertificateInstaller(_store);
        var progress = new CollectingProgress();

        var result = installer.EnsureInstalled(_root, progress.Report);

        Assert.True(result.Success);
        var imported = Assert.Single(_store.Certificates);
        Assert.Equal(certificate.Thumbprint, imported.Thumbprint);
        Assert.Contains("已安装 EAC 信任证书", result.Message);
        Assert.Contains(progress.Messages, message => message.Contains("安装 EAC 信任证书"));
    }

    [Fact]
    public void ForeignCertificateInZip_ShouldNotBeImported()
    {
        using var certificate = CreateCert("CN=Some Other CA");
        WriteKitZip("random-bundle.zip", certificate);

        var result = new EacCertificateInstaller(_store).EnsureInstalled(_root);

        Assert.True(result.Success);
        Assert.Null(result.Message);
        Assert.Empty(_store.Certificates);
    }

    [Fact]
    public void CorruptedCertificateEntry_ShouldBeSkippedWithoutThrowing()
    {
        WriteKitZip("MDL3-EAC-Fix-Kit-1.0.2.zip", corruptedBytes: [1, 2, 3, 4, 5]);

        var result = new EacCertificateInstaller(_store).EnsureInstalled(_root);

        Assert.True(result.Success);
        Assert.Null(result.Message);
        Assert.Empty(_store.Certificates);
    }

    [Fact]
    public void CorruptedZipFile_ShouldNotPreventOtherSources()
    {
        var corruptedZip = Path.Combine(_root, "broken.zip");
        File.WriteAllBytes(corruptedZip, [0x50, 0x4b, 0x03, 0x04, 0x00]);
        using var certificate = CreateCert($"CN={EacCertificateInstaller.ExpectedSubjectFragment}");
        WriteKitZip("MDL3-EAC-Fix-Kit-1.0.2.zip", certificate);

        var result = new EacCertificateInstaller(_store).EnsureInstalled(_root);

        Assert.True(result.Success);
        Assert.Single(_store.Certificates);
    }

    /// <summary>在 mod 目录写出含 tools/*.cer 的 Fix Kit 形态 zip；corruptedBytes 非空时写坏数据。</summary>
    private void WriteKitZip(string zipName, X509Certificate2? certificate = null, byte[]? corruptedBytes = null)
    {
        var staging = Path.Combine(_root, "staging");
        var tools = Path.Combine(staging, "MDL3-EAC-Fix-Kit-1.0.2", "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllBytes(Path.Combine(tools, "Certum_Code_Signing_2021_CA.cer"),
            corruptedBytes ?? certificate!.Export(X509ContentType.Cert));
        ZipFile.CreateFromDirectory(staging, Path.Combine(_root, zipName));
        Directory.Delete(staging, true);
    }

    private static X509Certificate2 CreateCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private sealed class FakeCertificateStore : IEacCertificateStore
    {
        public List<X509Certificate2> Certificates { get; } = [];

        public bool Contains(X509Certificate2 certificate)
            => Certificates.Any(item => item.Thumbprint.Equals(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase));

        public void Add(X509Certificate2 certificate) => Certificates.Add(certificate);
    }

    private sealed class CollectingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
