using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.SoundFix;

/// <summary>只读扫描库内文件；游戏 data 仅作为官方参考。</summary>
public sealed class SoundFixInspector(IGameSoundBankReader references)
{
    private sealed class Inspection
    {
        public SoundFixModReport Report { get; set; } = new();
        public Dictionary<string, Package> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<FixPlan> Plans { get; } = [];
    }

    public Task<SoundFixModReport> InspectAsync(Mod mod, string gameDirectory, CancellationToken token = default)
        => Task.Run(async () => (await InspectCoreAsync(mod, gameDirectory, token)).Report, token);

    public Task<SoundFixTransaction> PrepareAsync(Mod mod, string gameDirectory, SoundFixModReport expected,
        string backupRoot, CancellationToken token = default)
        => Task.Run(async () =>
        {
            var inspection = await InspectCoreAsync(mod, gameDirectory, token);
            if (!string.Equals(expected.ModPath, mod.Path, StringComparison.OrdinalIgnoreCase)
                || inspection.Report.Fingerprint != expected.Fingerprint)
                throw new InvalidOperationException("模组或游戏参考已改变，请重新扫描。");
            if (inspection.Report.Errors.Count > 0) throw new InvalidDataException(string.Join("\n", inspection.Report.Errors));
            var transaction = new SoundFixTransaction(mod.Id, mod.Path, backupRoot);
            try
            {
                foreach (var group in inspection.Plans.Where(p => p.Safe).GroupBy(p => p.PatchPath, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var banks = group.ToDictionary(p => p.Ref.Entry.FileId, SoundBankRepair.RebuildBank);
                    var rebuilt = SoundBankRepair.RebuildPackage(inspection.Packages[group.Key], banks);
                    await transaction.StageAsync(group.Key, rebuilt, inspection.Hashes[group.Key], token);
                }
                return transaction;
            }
            catch { transaction.Dispose(); throw; }
        }, token);

    private async Task<Inspection> InspectCoreAsync(Mod mod, string gameDirectory, CancellationToken token)
    {
        var inspection = new Inspection();
        var banks = new List<BankRef>();
        var reports = new List<SoundFixBankReport>();
        var errors = new List<string>();
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var root = Path.GetFullPath(mod.Path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(path => Regex.IsMatch(Path.GetFileName(path), @"^[0-9a-fA-F]{16}(?:\.patch_\d+)?$"))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // 非音效补丁只读索引，不将整个模型/贴图包加载进内存。
                await using (var stream = File.OpenRead(path))
                {
                    byte[] header = new byte[72];
                    await stream.ReadExactlyAsync(header, token);
                    if (BitConverter.ToUInt32(header) != 0xf0000011) continue;
                    var types = BitConverter.ToUInt32(header, 4);
                    var count = BitConverter.ToUInt32(header, 8);
                    long indexSize = 72L + types * 32L + count * 80L;
                    if (indexSize > stream.Length || indexSize > 64 * 1024 * 1024) throw new InvalidDataException("补丁索引尺寸无效。");
                    stream.Position = 72L + types * 32L;
                    byte[] entry = new byte[80];
                    bool hasBank = false;
                    for (uint i = 0; i < count; i++)
                    {
                        await stream.ReadExactlyAsync(entry, token);
                        if (BitConverter.ToUInt64(entry, 8) == SoundBankRepair.BankType) { hasBank = true; break; }
                    }
                    if (!hasBank) continue;
                }
                var raw = await File.ReadAllBytesAsync(path, token);
                var hash = Convert.ToHexString(SHA256.HashData(raw));
                var package = Package.Load(raw, path);
                inspection.Packages.Add(path, package);
                inspection.Hashes.Add(path, hash);
                fingerprint.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path) + "\0" + hash));
                var entries = package.Entries.Where(e => e.TypeId == SoundBankRepair.BankType).ToArray();
                if (entries.Select(e => e.FileId).Distinct().Count() != entries.Length) throw new InvalidDataException("补丁包含重复音库 ID。");
                foreach (var entry in entries)
                    banks.Add(new BankRef { Package = package, PatchPath = path, Entry = entry,
                        BankName = package.DepName(entry.FileId) ?? entry.FileId.ToString("x16") });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
            { errors.Add($"{Path.GetRelativePath(root, path)}: {ex.Message}"); }
        }
        var official = await references.ReadSoundBanksAsync(gameDirectory, banks.Select(b => b.Entry.FileId).Distinct().ToArray(), token);
        foreach (var bank in banks)
        {
            token.ThrowIfCancellationRequested();
            var reference = official.GetValueOrDefault(bank.Entry.FileId);
            fingerprint.AppendData(Encoding.UTF8.GetBytes(bank.Entry.FileId.ToString("x16") + "\0"));
            if (reference?.Data is { } payload) fingerprint.AppendData(SHA256.HashData(payload));
            SoundFixBankReport Report(SoundFixBankState state, string? error = null, FixPlan? plan = null) => new()
            {
                PatchPath = Path.GetRelativePath(root, bank.PatchPath), ResourceId = bank.Entry.FileId, Name = bank.BankName,
                State = state, Error = error, MissingEntries = plan?.MissingEntries ?? 0, MissingMedia = plan?.MissingMedia ?? 0,
                CustomEntries = plan?.OnlyModEntries ?? 0, CustomMedia = plan?.OnlyModMedia ?? 0
            };
            if (reference?.Data is null) { reports.Add(Report(SoundFixBankState.Unknown, reference?.Error ?? "未取得官方音库参考。")); continue; }
            if (bank.Entry.StreamSize > 0 || bank.Entry.GpuSize > 0 || bank.BankName.EndsWith("/Init", StringComparison.OrdinalIgnoreCase)
                || bank.BankName.EndsWith("/game_init", StringComparison.OrdinalIgnoreCase))
            { reports.Add(Report(SoundFixBankState.Skipped, "初始化库或流式音库不在自动修复范围。")); continue; }
            try
            {
                var plan = SoundBankRepair.Evaluate(bank, reference.Data);
                if (plan is null) reports.Add(Report(SoundFixBankState.Current));
                else
                {
                    inspection.Plans.Add(plan);
                    reports.Add(Report(plan.Safe ? SoundFixBankState.Repairable : SoundFixBankState.Skipped,
                        plan.Safe ? null : "含较多或非武器类自定义条目，保留原作者默认跳过规则。", plan));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            { reports.Add(Report(SoundFixBankState.Error, ex.Message)); errors.Add($"{bank.BankName}: {ex.Message}"); }
        }
        inspection.Report = new SoundFixModReport { ModId = mod.Id, Name = mod.Name, ModPath = mod.Path,
            Banks = reports, Errors = errors, Fingerprint = Convert.ToHexString(fingerprint.GetHashAndReset()) };
        return inspection;
    }
}
