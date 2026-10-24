using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.SoundFix;

namespace HD2Mimnt.Application.Services;

public sealed class SoundFixService(IModRepository repository, ModService mods, SoundFixInspector inspector,
    SettingsService settings, string backupRoot)
{
    public const string AuthorUrl = "https://space.bilibili.com/157681854";

    public async Task<IReadOnlyList<SoundFixModReport>> ScanLibraryAsync(IProgress<SoundFixProgress>? progress = null, CancellationToken token = default)
    {
        var game = RequireGamePath();
        var library = (await repository.GetAllAsync(token)).ToArray();
        var results = new List<SoundFixModReport>();
        for (var i = 0; i < library.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var mod = library[i];
            progress?.Report(new(i, library.Length, mod.Name));
            SoundFixModReport report;
            try { report = await inspector.InspectAsync(mod, game, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { report = new() { ModId = mod.Id, Name = mod.Name, ModPath = mod.Path, Errors = [ex.Message] }; }
            if (report.Banks.Count > 0 || report.Errors.Count > 0 || await HasBackupAsync(mod.Id, token)) results.Add(report);
            progress?.Report(new(i + 1, library.Length, mod.Name));
        }
        return results;
    }

    public Task<bool> HasBackupAsync(Guid id, CancellationToken token = default)
        => Task.Run(() => SoundFixTransaction.HasBackup(id, backupRoot), token);

    public Task<SoundFixResult> RepairAsync(SoundFixModReport report, CancellationToken token = default)
    {
        if (!report.CanRepair) throw new InvalidOperationException("没有可安全修复的音库。");
        var game = RequireGamePath();
        return mods.MutateSoundFixAsync(report.ModId, mod => inspector.PrepareAsync(mod, game, report, backupRoot, token), token);
    }

    public Task<SoundFixResult> RestoreAsync(Guid id, CancellationToken token = default)
        => mods.MutateSoundFixAsync(id, mod => Task.Run(() => SoundFixTransaction.PrepareRestoreAsync(id, mod.Path, backupRoot, token), token), token);

    private string RequireGamePath()
    {
        var path = settings.GetEffectiveGamePath();
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请先在设置中配置游戏目录，以读取当前官方音库。");
        return path;
    }
}
