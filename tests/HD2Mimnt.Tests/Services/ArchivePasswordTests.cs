using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Archive;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class ArchivePasswordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-PasswordTests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _stored = [];
    private readonly Mock<ISettingsRepository> _repository = new();
    private readonly SettingsService _settings;
    private readonly ArchivePasswordService _passwords;

    public ArchivePasswordTests()
    {
        Directory.CreateDirectory(_root);
        _repository.Setup(r => r.IsLegacyMigrationComplete()).Returns(true);
        _repository.Setup(r => r.ReadAll()).Returns(() => new(_stored));
        _repository.Setup(r => r.ApplyAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns((IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> removed) =>
            {
                foreach (var key in removed) _stored.Remove(key);
                foreach (var pair in values) _stored[pair.Key] = pair.Value;
                return Task.CompletedTask;
            });
        _settings = new(_repository.Object, _root);
        _passwords = new(_settings);
    }

    private async Task<string> CreateArchiveAsync(string extension, string password)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "测试.patch_0"), "完整的加密内容");
        var service = new ModCreationService();
        var document = service.Create();
        document.SourceDirectory = source;
        document.Manifest.Name = "Encrypted mod";
        var output = Path.Combine(_root, "encrypted." + extension);
        await service.ExportArchiveAsync(document, output, ModArchiveCompression.Fast, password);
        return output;
    }

    [Fact]
    public async Task Library_ShouldEncryptPersistUpdateDefaultAndDeleteWithoutPlaintext()
    {
        const string secret = "secret 中文 password";
        var entry = await _passwords.SaveAsync("常用", secret, true);
        var persisted = Assert.Single(_stored).Value;
        Assert.DoesNotContain(secret, persisted);
        Assert.DoesNotContain("常用", persisted);
        Assert.DoesNotContain(secret, entry.ToString());
        var restarted = new ArchivePasswordService(new SettingsService(_repository.Object, _root));
        Assert.Equal(entry, Assert.Single(restarted.Read().Entries));
        Assert.Equal(entry.Id, restarted.Read().DefaultId);
        var updated = await _passwords.SaveAsync("常用", "new value", false);
        Assert.Equal(entry.Id, updated.Id);
        Assert.Null(_passwords.Read().DefaultId);
        await _passwords.SetDefaultAsync(entry.Id);
        await _passwords.DeleteAsync(entry.Id);
        Assert.Empty(_passwords.Read().Entries);
        Assert.Null(_passwords.Read().DefaultId);
    }

    [Fact]
    public async Task Library_FailedWrite_ShouldKeepPreviousPasswordAndDefault()
    {
        var entry = await _passwords.SaveAsync("常用", "original", true);
        _repository.Setup(r => r.ApplyAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .ThrowsAsync(new IOException("disk failure"));
        await Assert.ThrowsAsync<IOException>(() => _passwords.SaveAsync("常用", "replacement", false));
        Assert.Equal(entry, Assert.Single(_passwords.Read().Entries));
        Assert.Equal(entry.Id, _passwords.Read().DefaultId);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public async Task EncryptedArchive_ShouldRejectMissingAndWrongPasswordsThenRoundTripUnicode(string extension)
    {
        var password = extension == "7z" ? "中文 密码!\"#$-p🙂" : "ASCII password!\"#$-p";
        var archive = await CreateArchiveAsync(extension, password);
        var extractor = new SharpCompressArchiveExtractor();
        var destination = Path.Combine(_root, "extracted");
        await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() => extractor.ExtractToDirectoryAsync(archive, destination));
        await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() => extractor.ExtractWithPasswordAsync(archive, destination, "wrong"));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(_root, "*.extract"));
        await extractor.ExtractWithPasswordAsync(archive, destination, password);
        Assert.Equal("完整的加密内容", await File.ReadAllTextAsync(Path.Combine(destination, "测试.patch_0")));
        Assert.True(File.Exists(Path.Combine(destination, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(destination, "hd2mimnt.json")));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(_root, "*.extract"));
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public async Task Import_ShouldTryWrongDefaultThenRemainingSavedPassword(string extension)
    {
        var archive = await CreateArchiveAsync(extension, "correct");
        await _passwords.SaveAsync("正确", "correct", false);
        await _passwords.SaveAsync("默认但不匹配", "wrong", true);
        var extractor = new PasswordAwareArchiveExtractor(_passwords);
        var destination = Path.Combine(_root, "imported");
        await extractor.ExtractToDirectoryAsync(archive, destination);
        Assert.Equal("完整的加密内容", await File.ReadAllTextAsync(Path.Combine(destination, "测试.patch_0")));
    }

    [Fact]
    public async Task Import_Prompt_ShouldAllowCorrectionAndCancelWaitingWithoutLeavingFiles()
    {
        var archive = await CreateArchiveAsync("zip", "correct");
        var extractor = new PasswordAwareArchiveExtractor(_passwords);
        var requests = 0;
        using (PasswordAwareArchiveExtractor.BeginRequest((name, token) =>
        {
            Assert.Equal("encrypted.zip", name);
            return Task.FromResult(++requests == 1 ? "wrong" : "correct");
        }))
            await extractor.ExtractToDirectoryAsync(archive, Path.Combine(_root, "interactive"));
        Assert.Equal(2, requests);
        await Assert.ThrowsAsync<ArchivePasswordRequiredException>(() =>
            extractor.ExtractToDirectoryAsync(archive, Path.Combine(_root, "without-scope")));
        using var cancellation = new CancellationTokenSource();
        using (PasswordAwareArchiveExtractor.BeginRequest(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        }))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                extractor.ExtractToDirectoryAsync(archive, Path.Combine(_root, "cancelled"), cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "cancelled")));
        Assert.Empty(Directory.GetDirectories(_root, "*.extract"));
    }

    [Theory]
    [InlineData("zip", "中文密码")]
    [InlineData("7z", "line\nbreak")]
    public async Task Export_InvalidPassword_ShouldExplainAndPreserveExistingOutput(string extension, string password)
    {
        var output = Path.Combine(_root, "encrypted." + extension);
        await File.WriteAllTextAsync(output, "original destination");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CreateArchiveAsync(extension, password));
        Assert.Equal(extension == "zip" ? "ArchivePasswords.ZipAsciiRequired" : "ArchivePasswords.InvalidPassword", error.Message);
        Assert.Equal("original destination", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SevenZipWithEmptyFile_ShouldExtractWithoutRequestingPassword()
    {
        var source = Path.Combine(_root, "unencrypted-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "测试.patch_0"), "完整内容");
        await File.WriteAllBytesAsync(Path.Combine(source, "空文件.txt"), []);
        var service = new ModCreationService();
        var document = service.Create();
        document.SourceDirectory = source;
        document.Manifest.Name = "Unencrypted mod";
        var archive = Path.Combine(_root, "unencrypted.7z");
        await service.ExportArchiveAsync(document, archive, ModArchiveCompression.Fast);

        using var scope = PasswordAwareArchiveExtractor.BeginRequest((name, _) =>
            throw new InvalidOperationException($"不应为 {name} 请求密码"));
        var destination = Path.Combine(_root, "unencrypted-extracted");
        await new PasswordAwareArchiveExtractor(_passwords).ExtractToDirectoryAsync(archive, destination);

        Assert.Equal("完整内容", await File.ReadAllTextAsync(Path.Combine(destination, "测试.patch_0")));
        Assert.Empty(await File.ReadAllBytesAsync(Path.Combine(destination, "空文件.txt")));
    }

    [Fact]
    public async Task CorruptArchive_ShouldNotRequestPassword()
    {
        var archive = Path.Combine(_root, "broken.zip");
        await File.WriteAllTextAsync(archive, "not an archive");
        using var scope = PasswordAwareArchiveExtractor.BeginRequest((_, _) => throw new Exception("Should not prompt"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PasswordAwareArchiveExtractor(_passwords)
            .ExtractToDirectoryAsync(archive, Path.Combine(_root, "corrupt")));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
