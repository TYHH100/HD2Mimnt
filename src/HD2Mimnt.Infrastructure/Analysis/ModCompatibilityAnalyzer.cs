using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>
/// Stingray TOC、Unit/Layout/GPU 检查；元数据与已知资源格式迁移分别预览，复检后统一发布。
/// </summary>
public sealed partial class ModCompatibilityAnalyzer : IModCompatibilityAnalyzer
{
    private const uint PatchMagic = 0xF0000011;
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const ulong UnitTypeId = 0xe0a48d0be9a7453f;
    private static readonly SemaphoreSlim RepairGate = new(1, 1);
    private readonly IGameUnitReferenceReader _gameReader;

    public ModCompatibilityAnalyzer(IGameUnitReferenceReader? gameReader = null)
        => _gameReader = gameReader ?? new GameUnitReferenceReader();

    [GeneratedRegex(@"^[0-9a-fA-F]{16}\.patch_\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PatchFileRegex();

    public Task<ModCompatibilityReport> AnalyzeAsync(string modPath, uint? referenceVersion = null,
        Guid? modId = null, string? referenceSource = null, CancellationToken cancellationToken = default)
        => Task.Run(() => AnalyzeCoreAsync(modPath, referenceVersion, modId, referenceSource, cancellationToken), cancellationToken);

    public Task<CompatibilityRepairPlan> CreateRepairPlanAsync(string modPath, CancellationToken cancellationToken = default)
        => Task.Run(() => CreateRepairPlanCoreAsync(modPath, cancellationToken), cancellationToken);

    public Task<CompatibilityRepairResult> RepairAsync(string modPath, CancellationToken cancellationToken = default)
        => Task.Run(() => RepairCoreAsync(modPath, cancellationToken), cancellationToken);

    private async Task<ModCompatibilityReport> AnalyzeCoreAsync(
        string modPath,
        uint? referenceVersion = null,
        Guid? modId = null,
        string? referenceSource = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(modPath);
        var fullPath = Path.GetFullPath(modPath);
        var report = new ModCompatibilityReport
        {
            ModId = modId,
            ModPath = fullPath,
            ReferenceVersion = referenceVersion,
            ReferenceSource = referenceSource ?? (referenceVersion.HasValue ? "provided" : "unknown"),
            CheckedAtUtc = DateTime.UtcNow,
            Status = ModCompatibilityStatus.Unknown
        };

        if (!Directory.Exists(fullPath))
        {
            report.Status = ModCompatibilityStatus.Error;
            report.Issues.Add("模组目录不存在。");
            return report;
        }

        var patchFiles = EnumeratePatchFiles(fullPath, cancellationToken)
            .Where(path => PatchFileRegex().IsMatch(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (patchFiles.Length == 0)
        {
            report.Status = ModCompatibilityStatus.NotApplicable;
            report.Reason = ModCompatibilityReason.NoModelResources;
            return report;
        }

        foreach (var patchPath in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patch = await GetOrAnalyzePatchAsync(fullPath, patchPath, cancellationToken);
            report.Patches.Add(patch);
            report.Units.AddRange(patch.Units);
            foreach (var unit in patch.Units)
                report.VersionCounts[unit.Version] = report.VersionCounts.GetValueOrDefault(unit.Version) + 1;
            report.Issues.AddRange(patch.Issues.Select(issue => $"{patch.RelativePath}: {issue}"));
        }

        report.UnitCount = report.Units.Count;
        if (report.UnitCount == 0)
        {
            var noModels = report.Patches.All(patch => patch.ContainsUnitResources == false);
            report.Status = noModels ? ModCompatibilityStatus.NotApplicable : ModCompatibilityStatus.Unknown;
            report.Reason = noModels ? ModCompatibilityReason.NoModelResources : ModCompatibilityReason.ModelDataUnreadable;
        }
        else if (referenceVersion.HasValue)
        {
            report.ComparedUnitCount = report.UnitCount;
            report.Status = report.Units.Any(unit => unit.Version != referenceVersion.Value)
                ? ModCompatibilityStatus.Incompatible
                : ModCompatibilityStatus.Compatible;
        }
        else report.Reason = ModCompatibilityReason.ReferenceUnavailable;

        return report;
    }

    private async Task<CompatibilityRepairPlan> CreateRepairPlanCoreAsync(
        string modPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(modPath);
        var fullPath = Path.GetFullPath(modPath);
        var plan = new CompatibilityRepairPlan();
        if (!Directory.Exists(fullPath))
        {
            plan.BlockingReasons.Add("模组目录不存在。");
            return plan;
        }

        var patchFiles = EnumeratePatchFiles(fullPath, cancellationToken)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await BuildPatchRepairPlanAsync(path, plan, cancellationToken);
        }

        var distinctReasons = plan.BlockingReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        plan.BlockingReasons.Clear();
        plan.BlockingReasons.AddRange(distinctReasons);
        return plan;
    }

    private async Task<CompatibilityRepairResult> RepairCoreAsync(
        string modPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modPath);
        await RepairGate.WaitAsync(cancellationToken);
        try
        {
            var plan = await CreateRepairPlanAsync(modPath, cancellationToken);
            if (!plan.CanRepair)
            {
                return new CompatibilityRepairResult
                {
                    Success = plan.BlockingReasons.Count == 0,
                    Changed = false,
                    ErrorMessage = plan.BlockingReasons.Count == 0 ? null : string.Join(Environment.NewLine, plan.BlockingReasons)
                };
            }

            var prepared = new List<(string Original, string Temporary, string Backup)>();
            var committed = new List<(string Original, string Temporary, string Backup)>();
            try
            {
                foreach (var group in plan.Actions.GroupBy(action => action.PatchFilePath, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var original = new FileInfo(group.Key);
                    var temporary = Path.Combine(original.DirectoryName!, $".{original.Name}.hd2mimnt-repair-{Guid.NewGuid():N}.tmp");
                    var backup = CreateBackupPath(original);
                    prepared.Add((original.FullName, temporary, backup));
                    await CopyAsync(original.FullName, temporary, cancellationToken);
                    await ApplyActionsAsync(temporary, group.OrderBy(action => action.Offset).ToArray(), cancellationToken);
                    var validation = await AnalyzePatchAsync(original.DirectoryName!, temporary, cancellationToken, original.FullName);
                    if (!validation.IndexValid || !validation.PayloadRangesValid || !validation.EntryIndicesValid
                        || !validation.TypeDistributionValid || validation.Health == CompatibilityPatchHealth.Corrupted)
                        throw new InvalidDataException($"修复后复检失败：{original.Name}");
                }

                foreach (var item in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Replace(item.Temporary, item.Original, item.Backup, ignoreMetadataErrors: true);
                    committed.Add(item);
                }

                return new CompatibilityRepairResult
                {
                    Success = true,
                    Changed = true,
                    AppliedActionCount = plan.ActionCount,
                    BackupPaths = prepared.Select(item => item.Backup).ToList()
                };
            }
            catch (Exception ex)
            {
                var recoveryErrors = new List<string>();
                foreach (var item in committed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (File.Exists(item.Backup))
                            File.Copy(item.Backup, item.Original, overwrite: true);
                    }
                    catch (Exception rollbackError)
                    {
                        recoveryErrors.Add($"{item.Original}: {rollbackError.Message}");
                    }
                }
                if (ex is OperationCanceledException && recoveryErrors.Count == 0) throw;
                return new CompatibilityRepairResult { ErrorMessage = ex.Message,
                    Changed = recoveryErrors.Count > 0, RecoveryErrors = recoveryErrors,
                    BackupPaths = committed.Select(item => item.Backup).ToList() };
            }
            finally
            {
                foreach (var item in prepared)
                {
                    if (File.Exists(item.Temporary))
                    {
                        try { File.Delete(item.Temporary); } catch { }
                    }
                }
            }
        }
        finally
        {
            RepairGate.Release();
        }
    }

    private static readonly ConcurrentDictionary<string, CompatibilityPatchReport> PatchAnalysisCache = new();
    private const int MaxCachedPatchAnalyses = 512;

    /// <summary>
    /// 检查路径的 patch 报告缓存：键含主补丁与伴随文件的长度+mtime，命中返回深拷贝。
    /// 修复/验证路径（companionSource 非空或临时文件）不缓存；上限超限整体清空。
    /// </summary>
    private static async Task<CompatibilityPatchReport> GetOrAnalyzePatchAsync(string root, string path, CancellationToken token)
    {
        var key = PatchCacheKey(path);
        if (key.Length > 0 && PatchAnalysisCache.TryGetValue(key, out var cached))
            return ClonePatchReport(cached);
        var report = await AnalyzePatchAsync(root, path, token);
        if (key.Length > 0)
        {
            if (PatchAnalysisCache.Count >= MaxCachedPatchAnalyses) PatchAnalysisCache.Clear();
            // 缓存保留原始报告，调用方拿深拷贝；对报告/Unit 的后续修改不会污染缓存。
            PatchAnalysisCache[key] = report;
            return ClonePatchReport(report);
        }
        return report;
    }

    private static string PatchCacheKey(string path)
    {
        var main = new FileInfo(path);
        if (!main.Exists) return string.Empty;
        // 路径必须参与键：不同模组的补丁可能同长度同 mtime，仅凭指纹会互相串包。
        var key = new StringBuilder(path.Length * 3 + 64);
        key.Append(path);
        AppendFileFingerprint(key, main);
        AppendFileFingerprint(key, new FileInfo(path + ".stream"));
        AppendFileFingerprint(key, new FileInfo(path + ".gpu_resources"));
        return key.ToString();
    }

    private static void AppendFileFingerprint(StringBuilder builder, FileInfo file)
    {
        builder.Append('|');
        if (!file.Exists)
        {
            builder.Append("missing");
            return;
        }
        builder.Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
    }

    private static CompatibilityPatchReport ClonePatchReport(CompatibilityPatchReport report) => new()
    {
        RelativePath = report.RelativePath,
        ArchiveName = report.ArchiveName,
        FileSize = report.FileSize,
        Health = report.Health,
        ContainsUnitResources = report.ContainsUnitResources,
        HeaderValid = report.HeaderValid,
        IndexValid = report.IndexValid,
        PayloadRangesValid = report.PayloadRangesValid,
        EntryIndicesValid = report.EntryIndicesValid,
        TypeDistributionValid = report.TypeDistributionValid,
        RequiresStream = report.RequiresStream,
        RequiresGpuResources = report.RequiresGpuResources,
        HasStream = report.HasStream,
        HasGpuResources = report.HasGpuResources,
        TypeCount = report.TypeCount,
        ResourceCount = report.ResourceCount,
        Units = report.Units.Select(unit => new CompatibilityUnit
        {
            FileName = unit.FileName,
            ResourceId = unit.ResourceId,
            Version = unit.Version,
            DataSize = unit.DataSize,
            StreamSize = unit.StreamSize,
            GpuSize = unit.GpuSize,
            Analysis = unit.Analysis
        }).ToList(),
        Issues = [.. report.Issues]
    };

    private static async Task<CompatibilityPatchReport> AnalyzePatchAsync(
        string root, string path, CancellationToken cancellationToken, string? companionSource = null)
    {
        var info = new FileInfo(path);
        var result = new CompatibilityPatchReport
        {
            RelativePath = Path.GetRelativePath(root, path),
            FileSize = info.Exists ? info.Length : 0,
            Health = CompatibilityPatchHealth.Corrupted
        };

        try
        {
            if (!info.Exists || info.Length < HeaderSize)
                throw new InvalidDataException("补丁头部不完整。");

            var raw = await ReadPatchMetadataAsync(path, cancellationToken, companionSource);
            if (raw.IndexValid)
                result.ContainsUnitResources = raw.Types.Any(type => type.TypeId == UnitTypeId && type.Count > 0)
                    || raw.Entries.Any(entry => entry.TypeId == UnitTypeId);
            result.HeaderValid = raw.HeaderValid;
            result.IndexValid = raw.IndexValid;
            result.PayloadRangesValid = raw.PayloadRangesValid;
            result.EntryIndicesValid = raw.EntryIndicesValid;
            result.TypeDistributionValid = raw.TypeDistributionValid;
            result.TypeCount = raw.TypeCount;
            result.ResourceCount = raw.ResourceCount;
            result.RequiresStream = raw.RequiresStream;
            result.RequiresGpuResources = raw.RequiresGpuResources;
            result.Units.AddRange(raw.Units);

            var streamPath = (companionSource ?? path) + ".stream";
            var gpuPath = (companionSource ?? path) + ".gpu_resources";
            result.HasStream = File.Exists(streamPath);
            result.HasGpuResources = File.Exists(gpuPath);
            if (result.RequiresStream && !result.HasStream)
                result.Issues.Add("缺少必需的 .stream 伴随文件。");
            if (result.RequiresGpuResources && !result.HasGpuResources)
                result.Issues.Add("缺少必需的 .gpu_resources 伴随文件。");
            result.Issues.AddRange(raw.Issues);

            if (!result.HeaderValid || !result.IndexValid || !result.PayloadRangesValid || !result.TypeDistributionValid)
                result.Health = CompatibilityPatchHealth.Corrupted;
            else if (result.Units.Count == 0)
                result.Health = CompatibilityPatchHealth.NoUnitResources;
            else if (result.Issues.Count > 0 || !result.EntryIndicesValid)
                result.Health = CompatibilityPatchHealth.Warning;
            else
                result.Health = CompatibilityPatchHealth.Healthy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result.Issues.Add(ex.Message);
        }

        return result;
    }

    private static async Task BuildPatchRepairPlanAsync(
        string path, CompatibilityRepairPlan plan, CancellationToken cancellationToken,
        string? companionSource = null, IReadOnlyDictionary<string, string>? companionOverrides = null)
    {
        try
        {
            var raw = await ReadPatchMetadataAsync(path, cancellationToken, companionSource, companionOverrides, false);
            if (!raw.HeaderValid || !raw.IndexValid)
            {
                plan.BlockingReasons.Add($"{Path.GetFileName(path)}: 补丁结构无法安全推断，跳过修复。");
                return;
            }

            await AddExtendedMetadataRepairsAsync(path, raw, plan, cancellationToken, companionSource, companionOverrides);
            if (!raw.TypeGroupsMatchDeclaration)
            {
                var groups = raw.Entries.GroupBy(e => e.TypeId).ToArray();
                var contiguous = raw.Entries.Select((e, i) => i == 0 || e.TypeId != raw.Entries[i - 1].TypeId).Count(v => v);
                if (groups.Length != raw.Types.Count || contiguous != groups.Length)
                { plan.BlockingReasons.Add($"{Path.GetFileName(path)}: 资源类型分组顺序无法安全推断。"); return; }
                for (var i = 0; i < groups.Length; i++)
                {
                    var type = raw.Types[i];
                    if (type.TypeId != groups[i].Key)
                        plan.Actions.Add(new CompatibilityRepairAction { Kind = CompatibilityRepairKind.ResourceTypeId,
                            PatchFilePath = path, Offset = type.TableOffset + 8, Width = 8,
                            OldValue = type.TypeId, NewValue = groups[i].Key });
                    raw.Types[i] = type with { TypeId = groups[i].Key };
                }
            }

            foreach (var entry in raw.Entries)
            {
                if (entry.EntryIndex != (uint)(entry.Position + 1))
                {
                    plan.Actions.Add(new CompatibilityRepairAction
                    {
                        Kind = CompatibilityRepairKind.EntryIndex,
                        PatchFilePath = path,
                        Offset = entry.TableOffset + 76,
                        Width = 4,
                        OldValue = entry.EntryIndex,
                        NewValue = (uint)(entry.Position + 1)
                    });
                }
            }

            foreach (var type in raw.Types)
            {
                var count = raw.ActualTypeCounts.GetValueOrDefault(type.TypeId);
                if (type.Count != (ulong)count)
                {
                    plan.Actions.Add(new CompatibilityRepairAction
                    {
                        Kind = CompatibilityRepairKind.TypeResourceCount,
                        PatchFilePath = path,
                        Offset = type.TableOffset + 16,
                        Width = 8,
                        OldValue = type.Count,
                        NewValue = (ulong)count
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            plan.BlockingReasons.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static async Task ApplyActionsAsync(
        string path, IReadOnlyList<CompatibilityRepairAction> actions, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = new byte[action.Width];
            stream.Position = action.Offset;
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            var current = action.Width switch
            {
                4 => BinaryPrimitives.ReadUInt32LittleEndian(buffer),
                8 => BinaryPrimitives.ReadUInt64LittleEndian(buffer),
                _ => throw new InvalidDataException("不支持的修复字段宽度。")
            };
            if (current != action.OldValue)
                throw new InvalidDataException($"修复目标已变化：0x{action.Offset:X}。");
            if (action.Width == 4)
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)action.NewValue));
            else
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, action.NewValue);
            stream.Position = action.Offset;
            await stream.WriteAsync(buffer, cancellationToken);
        }
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static string CreateBackupPath(FileInfo file)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(file.DirectoryName!, $"{file.Name}.{stamp}.hd2mimnt-backup");
        var suffix = 1;
        while (File.Exists(candidate))
            candidate = Path.Combine(file.DirectoryName!, $"{file.Name}.{stamp}-{suffix++}.hd2mimnt-backup");
        return candidate;
    }

    private static async Task<PatchMetadata> ReadPatchMetadataAsync(
        string path, CancellationToken cancellationToken, string? companionSource = null,
        IReadOnlyDictionary<string, string>? companionOverrides = null, bool inspectUnits = true)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var result = new PatchMetadata
        {
            HeaderValid = magic == PatchMagic,
            TypeCount = typeCount <= int.MaxValue ? (int)typeCount : 0,
            ResourceCount = fileCount <= int.MaxValue ? (int)fileCount : 0
        };
        if (!result.HeaderValid || typeCount > 1000 || fileCount > 100000)
        {
            result.Issues.Add("补丁头部计数无效。");
            return result;
        }

        var fileEntriesOffset = HeaderSize + (long)typeCount * TypeEntrySize;
        var tableEnd = fileEntriesOffset + (long)fileCount * FileEntrySize;
        result.IndexValid = tableEnd <= stream.Length;
        if (!result.IndexValid)
        {
            result.Issues.Add("补丁索引超出文件边界。");
            return result;
        }

        var tables = new byte[checked((int)tableEnd - HeaderSize)];
        await stream.ReadExactlyAsync(tables, cancellationToken);
        for (var i = 0; i < typeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeBuffer = tables.AsSpan(i * TypeEntrySize, TypeEntrySize);
            result.Types.Add(new(
                BinaryPrimitives.ReadUInt64LittleEndian(typeBuffer[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(typeBuffer[16..]),
                HeaderSize + (long)i * TypeEntrySize,
                BinaryPrimitives.ReadUInt32LittleEndian(typeBuffer[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(typeBuffer[28..])));
        }

        var actualGroups = new List<(ulong TypeId, int Count)>();
        for (var i = 0; i < fileCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tableOffset = fileEntriesOffset + (long)i * FileEntrySize;
            var entryBuffer = tables.AsSpan(checked((int)(tableOffset - HeaderSize)), FileEntrySize);
            var entry = new PatchEntry(
                i,
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer[32..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[56..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[60..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[64..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer[76..]),
                tableOffset);
            result.Entries.Add(entry);
            result.ActualTypeCounts[entry.TypeId] = result.ActualTypeCounts.GetValueOrDefault(entry.TypeId) + 1;
            if (actualGroups.Count == 0 || actualGroups[^1].TypeId != entry.TypeId)
                actualGroups.Add((entry.TypeId, 1));
            else
                actualGroups[^1] = (entry.TypeId, actualGroups[^1].Count + 1);
            result.RequiresStream |= entry.StreamSize > 0;
            result.RequiresGpuResources |= entry.GpuSize > 0;

            if (inspectUnits && entry.TypeId == UnitTypeId && entry.DataSize >= 0x30
                && entry.DataOffset >= (ulong)tableEnd
                && InBounds(entry.DataOffset, entry.DataSize, stream.Length))
            {
                stream.Position = checked((long)entry.DataOffset + 0x2C);
                var version = new byte[4];
                await stream.ReadExactlyAsync(version, cancellationToken);
                var unit = new CompatibilityUnit
                {
                    FileName = Path.GetFileName(path),
                    ResourceId = entry.ResourceId,
                    Version = BinaryPrimitives.ReadUInt32LittleEndian(version),
                    DataSize = entry.DataSize,
                    StreamSize = entry.StreamSize,
                    GpuSize = entry.GpuSize
                };
                unit.Analysis = await InspectUnitAsync(stream, entry,
                    companionOverrides?.GetValueOrDefault(".gpu_resources") ?? (companionSource ?? path) + ".gpu_resources", cancellationToken);
                result.Units.Add(unit);
                result.Issues.AddRange(unit.Analysis.Errors.Concat(unit.Analysis.Warnings)
                    .Select(issue => $"Unit {entry.ResourceId:x16}: {issue}"));
            }
        }

        result.EntryIndicesValid = result.Entries.All(entry => entry.EntryIndex == (uint)(entry.Position + 1));
        result.TypeDistributionValid = result.Types.Select(type => type.TypeId).Distinct().Count() == result.Types.Count
            && result.Types.All(type => (ulong)result.ActualTypeCounts.GetValueOrDefault(type.TypeId) == type.Count)
            && result.ActualTypeCounts.Keys.All(typeId => result.Types.Any(type => type.TypeId == typeId));
        result.TypeGroupsMatchDeclaration = result.Types.Select(type => type.TypeId).Distinct().Count() == result.Types.Count
            && actualGroups.Select(group => group.TypeId).Distinct().Count() == actualGroups.Count
            && actualGroups.Select(group => group.TypeId).SequenceEqual(result.Types
                .Where(type => result.ActualTypeCounts.ContainsKey(type.TypeId)).Select(type => type.TypeId));
        var companion = companionSource ?? path;
        var streamPath = companionOverrides?.GetValueOrDefault(".stream") ?? companion + ".stream";
        var gpuPath = companionOverrides?.GetValueOrDefault(".gpu_resources") ?? companion + ".gpu_resources";
        var streamLength = File.Exists(streamPath) ? new FileInfo(streamPath).Length : 0;
        var gpuLength = File.Exists(gpuPath) ? new FileInfo(gpuPath).Length : 0;
        result.PayloadRangesValid = true;
        var ranges = new List<(ulong Start, ulong End)>();
        foreach (var entry in result.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!InBounds(entry.DataOffset, entry.DataSize, stream.Length)
                || entry.DataSize > 0 && entry.DataOffset < (ulong)tableEnd
                || entry.StreamSize > 0 && !InBounds(entry.StreamOffset, entry.StreamSize, streamLength)
                || entry.GpuSize > 0 && !InBounds(entry.GpuOffset, entry.GpuSize, gpuLength)
                || entry.TypeId == UnitTypeId && entry.DataSize < 0x30)
                result.PayloadRangesValid = false;
            else if (entry.DataSize > 0)
                ranges.Add((entry.DataOffset, entry.DataOffset + entry.DataSize));
        }
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        ulong end = 0;
        foreach (var range in ranges)
        {
            if (range.Start < end) result.PayloadRangesValid = false;
            end = Math.Max(end, range.End);
        }
        if (!result.PayloadRangesValid) result.Issues.Add("资源主数据或伴随数据越界、缺失或重叠。");
        if (!result.EntryIndicesValid)
            result.Issues.Add("TOC entry_index 不是连续序列。");
        if (!result.TypeDistributionValid)
            result.Issues.Add("类型表计数与实际资源条目不一致。");
        return result;
    }

    private static bool InBounds(ulong offset, uint size, long length)
        => offset <= (ulong)length && size <= (ulong)length - offset;

    private static IEnumerable<string> EnumeratePatchFiles(string root, CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            token.ThrowIfCancellationRequested();
            if (PatchFileRegex().IsMatch(Path.GetFileName(path))) yield return path;
        }
    }

    private static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, token);
    }

    public Task<IReadOnlyList<string>> GetBackupsAsync(string modPath, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<string>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(modPath);
            return Directory.EnumerateFiles(root, "*.hd2mimnt-backup", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(path => { cancellationToken.ThrowIfCancellationRequested(); return BackupOriginal(path) != null; })
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        }, cancellationToken);

    public Task<CompatibilityRepairResult> RestoreBackupAsync(string modPath, string backupPath, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await RepairGate.WaitAsync(cancellationToken);
            try { return await RestoreEntriesAsync(modPath, [backupPath], cancellationToken); }
            finally { RepairGate.Release(); }
        }, cancellationToken);
    private static string? BackupOriginal(string path)
    {
        var name = Path.GetFileName(path);
        var match = Regex.Match(name, @"^([0-9a-fA-F]{16}\.patch_\d+(?:\.stream|\.gpu_resources)?)\.\d{8}-\d{6}(?:-\d+)?\.hd2mimnt-backup$",
            RegexOptions.CultureInvariant);
        return match.Success ? Path.Combine(Path.GetDirectoryName(path)!, match.Groups[1].Value) : null;
    }

    private sealed class PatchMetadata
    {
        public bool HeaderValid { get; init; }
        public bool IndexValid { get; set; }
        public bool PayloadRangesValid { get; set; }
        public bool EntryIndicesValid { get; set; }
        public bool TypeDistributionValid { get; set; }
        public bool TypeGroupsMatchDeclaration { get; set; }
        public int TypeCount { get; init; }
        public int ResourceCount { get; init; }
        public bool RequiresStream { get; set; }
        public bool RequiresGpuResources { get; set; }
        public List<string> Issues { get; } = [];
        public List<PatchType> Types { get; } = [];
        public List<PatchEntry> Entries { get; } = [];
        public Dictionary<ulong, int> ActualTypeCounts { get; } = [];
        public List<CompatibilityUnit> Units { get; } = [];
    }

    private readonly record struct PatchType(ulong TypeId, ulong Count, long TableOffset, uint MainAlignment, uint GpuAlignment);
    private readonly record struct PatchEntry(int Position, ulong ResourceId, ulong TypeId, ulong DataOffset,
        ulong StreamOffset, ulong GpuOffset, uint DataSize, uint StreamSize, uint GpuSize, uint EntryIndex,
        long TableOffset);
}
