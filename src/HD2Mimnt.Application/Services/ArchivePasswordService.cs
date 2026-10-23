using System.Text.Json;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Archive;

namespace HD2Mimnt.Application.Services;

public sealed record ArchivePasswordEntry(Guid Id, string Name, string Password)
{
    public override string ToString() => Name;
}
public sealed record ArchivePasswordLibrary(List<ArchivePasswordEntry> Entries, Guid? DefaultId);

/// <summary>密码库在设置库中只保存 DPAPI 密文，默认密码与条目一次提交。</summary>
public sealed class ArchivePasswordService(SettingsService settings)
{
    public const string SettingKey = "ArchivePasswords.ProtectedLibrary";
    private static readonly SemaphoreSlim MutationGate = new(1, 1);

    public ArchivePasswordLibrary Read()
    {
        var value = settings.GetSetting(SettingKey, "");
        return value.Length == 0 ? new([], null)
            : JsonSerializer.Deserialize<ArchivePasswordLibrary>(ArchivePasswordProtection.Unprotect(value))
                ?? throw new InvalidDataException("ArchivePasswords.InvalidLibrary");
    }

    public async Task<ArchivePasswordEntry> SaveAsync(string name, string password, bool makeDefault)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password))
            throw new InvalidDataException("ArchivePasswords.NameAndPasswordRequired");
        if (password.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new InvalidDataException("ArchivePasswords.InvalidPassword");
        await MutationGate.WaitAsync();
        try
        {
            var library = Read();
            var existing = library.Entries.Find(e => e.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            var entry = new ArchivePasswordEntry(existing?.Id ?? Guid.NewGuid(), name.Trim(), password);
            library.Entries.RemoveAll(e => e.Id == entry.Id);
            library.Entries.Add(entry);
            await WriteAsync(library with { DefaultId = makeDefault ? entry.Id : library.DefaultId == entry.Id ? null : library.DefaultId });
            return entry;
        }
        finally { MutationGate.Release(); }
    }

    public async Task DeleteAsync(Guid id)
    {
        await MutationGate.WaitAsync();
        try
        {
            var library = Read();
            library.Entries.RemoveAll(e => e.Id == id);
            await WriteAsync(library with { DefaultId = library.DefaultId == id ? null : library.DefaultId });
        }
        finally { MutationGate.Release(); }
    }

    public async Task SetDefaultAsync(Guid? id)
    {
        await MutationGate.WaitAsync();
        try
        {
            var library = Read();
            if (id != null && !library.Entries.Any(e => e.Id == id)) throw new InvalidDataException("ArchivePasswords.InvalidLibrary");
            await WriteAsync(library with { DefaultId = id });
        }
        finally { MutationGate.Release(); }
    }

    private Task WriteAsync(ArchivePasswordLibrary library) => settings.SetSettingAsync(SettingKey,
        ArchivePasswordProtection.Protect(JsonSerializer.Serialize(library)));
}

/// <summary>回调仅作用于当前导入异步调用链，包含嵌套包，不影响其它导入任务。</summary>
public sealed class PasswordAwareArchiveExtractor(ArchivePasswordService passwords) : IArchiveExtractor
{
    private readonly SharpCompressArchiveExtractor _extractor = new();
    private static readonly AsyncLocal<Func<string, CancellationToken, Task<string>>?> Request = new();

    public static IDisposable BeginRequest(Func<string, CancellationToken, Task<string>> request)
    {
        var previous = Request.Value;
        Request.Value = request;
        return new RequestScope(() => Request.Value = previous);
    }

    public bool IsArchiveFile(string path) => _extractor.IsArchiveFile(path);

    public async Task ExtractToDirectoryAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        string? password = null;
        Queue<string>? candidates = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _extractor.ExtractWithPasswordAsync(archivePath, destinationDirectory, password, cancellationToken);
                return;
            }
            catch (ArchivePasswordRequiredException)
            {
                // 每次提取使用独立暂存目录；失败副本已清理，不能混用前次解密的部分文件。
                if (candidates == null)
                {
                    var library = passwords.Read();
                    candidates = new(library.Entries.OrderByDescending(e => e.Id == library.DefaultId)
                        .Select(e => e.Password).Distinct(StringComparer.Ordinal));
                }
                password = null;
                while (candidates.TryDequeue(out var candidate))
                {
                    if (attempted.Add(candidate)) { password = candidate; break; }
                }
                if (password != null) continue;
                if (Request.Value == null) throw;
                password = await Request.Value(Path.GetFileName(archivePath), cancellationToken);
                if (string.IsNullOrEmpty(password)) throw new ArchivePasswordRequiredException();
            }
        }
    }

    private sealed class RequestScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
