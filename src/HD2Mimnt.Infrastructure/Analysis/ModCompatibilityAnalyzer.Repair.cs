using System.Buffers.Binary;
using System.Security.Cryptography;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class ModCompatibilityAnalyzer
{
    private const ulong MaterialTypeId = 0xeac0b497876adedf;
    private const int MaxRebuildBytes = 256 * 1024 * 1024;
    private sealed record PreparedFile(string Original, string Temporary, string? ExpectedHash);
    private sealed class RepairPreparation : IDisposable
    {
        public CompatibilityFullRepairPlan Plan { get; } = new();
        public List<PreparedFile> Files { get; } = [];
        public List<string> Temporaries { get; } = [];
        public void Dispose() { foreach (var path in Temporaries) if (File.Exists(path)) File.Delete(path); }
    }

    public Task<CompatibilityFullRepairPlan> CreateFullRepairPlanAsync(string modPath, string? gameDirectory,
        IReadOnlyList<string> sourceModPaths, CompatibilityRepairOptions options, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            using var prepared = await PrepareFullRepairAsync(modPath, gameDirectory, sourceModPaths, options, cancellationToken);
            return prepared.Plan;
        }, cancellationToken);

    public Task<CompatibilityRepairResult> RepairFullAsync(string modPath, string? gameDirectory,
        IReadOnlyList<string> sourceModPaths, CompatibilityRepairOptions options, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await RepairGate.WaitAsync(cancellationToken);
            try
            {
                using var prepared = await PrepareFullRepairAsync(modPath, gameDirectory, sourceModPaths, options, cancellationToken);
                if (prepared.Plan.BlockingReasons.Count > 0)
                    return new CompatibilityRepairResult { ErrorMessage = string.Join(Environment.NewLine, prepared.Plan.BlockingReasons) };
                return await CommitPreparedAsync(prepared.Files, prepared.Plan.ActionCount, cancellationToken);
            }
            finally { RepairGate.Release(); }
        }, cancellationToken);

    private async Task<RepairPreparation> PrepareFullRepairAsync(string modPath, string? gameDirectory,
        IReadOnlyList<string> sourceModPaths, CompatibilityRepairOptions options, CancellationToken token)
    {
        var prepared = new RepairPreparation();
        try
        {
            var root = Path.GetFullPath(modPath);
            var patches = EnumeratePatchFiles(root, token).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var sources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var staged = new List<(string Original, string Temporary, string? Hash, Dictionary<string, string> Companions)>();
            foreach (var original in patches)
            {
                token.ThrowIfCancellationRequested();
                var temporary = Path.Combine(Path.GetDirectoryName(original)!, $".{Path.GetFileName(original)}.hd2mimnt-work-{Guid.NewGuid():N}.tmp");
                prepared.Temporaries.Add(temporary);
                // GPU/stream 仅引用原路径或恢复后的暂存文件，不复制现有的大型伴随文件。
                string? hash = null;
                var raw = await ReadPatchMetadataAsync(original, token, inspectUnits: false);
                if (!raw.HeaderValid || !raw.IndexValid) { prepared.Plan.BlockingReasons.Add($"{original}: 补丁头或索引损坏。"); continue; }
                var companions = new Dictionary<string, string>();
                if (options.RecoverCompanions)
                    await PrepareCompanionsAsync(root, original, temporary, raw, gameDirectory, sourceModPaths, sources, hashes, companions, prepared, token);
                if (options.RepairMetadata)
                {
                    var metadata = new CompatibilityRepairPlan();
                    await BuildPatchRepairPlanAsync(original, metadata, token, original, companions);
                    prepared.Plan.BlockingReasons.AddRange(metadata.BlockingReasons);
                    foreach (var action in metadata.Actions)
                        prepared.Plan.Metadata.Actions.Add(new CompatibilityRepairAction { Kind = action.Kind,
                            PatchFilePath = original, Offset = action.Offset, Width = action.Width,
                            OldValue = action.OldValue, NewValue = action.NewValue });
                    if (metadata.BlockingReasons.Count == 0 && metadata.Actions.Count > 0)
                    {
                        await CopyAsync(original, temporary, token);
                        hash = await ShaAsync(temporary, token);
                        await ApplyActionsAsync(temporary, metadata.Actions, token);
                    }
                }
                staged.Add((original, temporary, hash, companions));
            }
            if (prepared.Plan.BlockingReasons.Count > 0) return prepared;

            GameUnitReferenceReport? references = null;
            if (options.RepairUnits)
            {
                var ids = new HashSet<ulong>();
                foreach (var patch in staged)
                {
                    var raw = await ReadPatchMetadataAsync(File.Exists(patch.Temporary) ? patch.Temporary : patch.Original, token, patch.Original, patch.Companions, false);
                    foreach (var entry in raw.Entries.Where(e => e.TypeId == UnitTypeId)) ids.Add(entry.ResourceId);
                }
                if (ids.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(gameDirectory)) prepared.Plan.BlockingReasons.Add("未配置游戏目录，无法生成 Unit 修复参考。");
                    else
                    {
                        references = await _gameReader.ReadDetailedAsync(gameDirectory, ids, token);
                        if (!references.IndexComplete) prepared.Plan.BlockingReasons.AddRange(references.Issues);
                    }
                }
            }
            if (prepared.Plan.BlockingReasons.Count > 0) return prepared;
            foreach (var patch in staged)
            {
                token.ThrowIfCancellationRequested();
                var input = File.Exists(patch.Temporary) ? patch.Temporary : patch.Original;
                var raw = await ReadPatchMetadataAsync(input, token, patch.Original, patch.Companions, false);
                if (!raw.PayloadRangesValid || !raw.TypeDistributionValid || !raw.EntryIndicesValid)
                { prepared.Plan.BlockingReasons.Add($"{patch.Original}: 结构或伴随范围仍无效。"); continue; }
                string? rebuiltHash = null;
                if (options.RepairUnits || options.MigrateMaterials)
                    rebuiltHash = await RebuildResourcesAsync(root, patch.Original, input, patch.Temporary, raw, references, options, prepared.Plan, token);
                var validation = await ReadPatchMetadataAsync(File.Exists(patch.Temporary) ? patch.Temporary : patch.Original, token, patch.Original, patch.Companions);
                if (!validation.PayloadRangesValid || !validation.EntryIndicesValid || !validation.TypeDistributionValid
                    || options.RepairUnits && validation.Units.Any(u => u.Analysis is { IsValid: false }))
                { prepared.Plan.BlockingReasons.Add($"{patch.Original}: 修复结果未通过结构或 Unit 深度复检。"); continue; }
                if (File.Exists(patch.Temporary))
                    prepared.Files.Add(new PreparedFile(patch.Original, patch.Temporary, patch.Hash ?? rebuiltHash
                        ?? throw new InvalidDataException("缺少原文件内容指纹。")));
            }
            return prepared;
        }
        catch (OperationCanceledException) { prepared.Dispose(); throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        { prepared.Plan.BlockingReasons.Add(ex.Message); return prepared; }
    }

    private async Task PrepareCompanionsAsync(string root, string original, string temporary, PatchMetadata raw,
        string? gameDirectory, IReadOnlyList<string> sourceModPaths, Dictionary<string, string[]> sources,
        Dictionary<string, string> hashes, Dictionary<string, string> companions, RepairPreparation prepared, CancellationToken token)
    {
        async Task<string> Hash(string path)
        {
            if (!hashes.TryGetValue(path, out var cached)) hashes[path] = cached = await ShaAsync(path, token);
            return cached;
        }
        foreach (var gpu in new[] { false, true })
        {
            var suffix = gpu ? ".gpu_resources" : ".stream";
            var entries = raw.Entries.Where(e => (gpu ? e.GpuSize : e.StreamSize) > 0).ToArray();
            if (entries.Length == 0) continue;
            var target = original + suffix;
            var targetLength = File.Exists(target) ? new FileInfo(target).Length : 0;
            if (File.Exists(target) && entries.All(e => InBounds(gpu ? e.GpuOffset : e.StreamOffset, gpu ? e.GpuSize : e.StreamSize, targetLength))) continue;
            var output = temporary + suffix;
            prepared.Temporaries.Add(output);
            var hash = await Hash(original);
            var name = Path.GetFileName(original);
            if (!sources.TryGetValue(name, out var candidates))
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sourceRoot in sourceModPaths.Append(root).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(sourceRoot)) continue;
                    foreach (var path in Directory.EnumerateFiles(sourceRoot, name, new EnumerationOptions
                        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })) paths.Add(path);
                }
                sources[name] = candidates = paths.ToArray();
            }
            var copied = false;
            string? selectedSource = null;
            string? selectedHash = null;
            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (string.Equals(Path.GetFullPath(candidate), original, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(candidate + suffix) || new FileInfo(candidate).Length != new FileInfo(original).Length) continue;
                if (await Hash(candidate) != hash) continue;
                var length = new FileInfo(candidate + suffix).Length;
                if (!entries.All(e => InBounds(gpu ? e.GpuOffset : e.StreamOffset, gpu ? e.GpuSize : e.StreamSize, length))) continue;
                var candidateHash = await Hash(candidate + suffix);
                if (selectedHash is not null && selectedHash != candidateHash)
                    throw new InvalidDataException($"{original}{suffix}: 主补丁一致的副本含有不同伴随内容，拒绝猜测来源。");
                selectedSource = candidate + suffix;
                selectedHash = candidateHash;
            }
            if (selectedSource is not null)
            {
                await CopyAsync(selectedSource, output, token);
                if (await ShaAsync(output, token) != selectedHash) throw new InvalidDataException("恢复来源在复制期间发生变化。");
                copied = true;
            }
            if (!copied && !string.IsNullOrWhiteSpace(gameDirectory))
            {
                var requests = new List<GameCompanionRequest>();
                await using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                foreach (var entry in entries)
                {
                    if (entry.DataSize > MaxUnitBytes) throw new InvalidDataException("用于伴随恢复的主资源过大。");
                    requests.Add(new GameCompanionRequest { ResourceId = entry.ResourceId, TypeId = entry.TypeId,
                        MainData = await ReadAtAsync(input, entry.DataOffset, (int)entry.DataSize, token),
                        Gpu = gpu, Size = gpu ? entry.GpuSize : entry.StreamSize });
                }
                var payloads = await _gameReader.ReadCompanionsAsync(gameDirectory, requests, token);
                if (payloads.Count == entries.Length && payloads.All(p => p.Payload is not null))
                {
                    var segments = entries.Select((entry, i) => (Offset: gpu ? entry.GpuOffset : entry.StreamOffset, Data: payloads[i].Payload!))
                        .OrderBy(e => e.Offset).ToArray();
                    ulong end = 0;
                    var normalized = new List<(ulong Offset, byte[] Data)>();
                    foreach (var segment in segments)
                    {
                        if (normalized.Count > 0 && segment.Offset == normalized[^1].Offset && segment.Data.AsSpan().SequenceEqual(normalized[^1].Data)) continue;
                        if (segment.Offset < end || segment.Offset > long.MaxValue - (ulong)segment.Data.Length)
                            throw new InvalidDataException("恢复的伴随片段重叠或超出文件范围。");
                        normalized.Add(segment);
                        end = segment.Offset + (ulong)segment.Data.Length;
                    }
                    await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    destination.SetLength(checked((long)end));
                    foreach (var segment in normalized) { destination.Position = (long)segment.Offset; await destination.WriteAsync(segment.Data, token); }
                    copied = true;
                }
            }
            if (!copied) { prepared.Plan.BlockingReasons.Add($"{original}{suffix}: 没有可证明内容一致的恢复来源。"); continue; }
            companions[suffix] = output;
            prepared.Files.Add(new PreparedFile(target, output, File.Exists(target) ? await ShaAsync(target, token) : null));
            prepared.Plan.Actions.Add(new CompatibilityContentAction { Kind = "CompanionRecovery", RelativePath = Path.GetRelativePath(root, target),
                OldSize = targetLength, NewSize = new FileInfo(output).Length, Description = "从主补丁完全一致的副本或逐字节一致的游戏资源恢复。" });
        }
    }

    private static async Task<string?> RebuildResourcesAsync(string root, string original, string inputPath, string temporary, PatchMetadata raw,
        GameUnitReferenceReport? references, CompatibilityRepairOptions options, CompatibilityFullRepairPlan plan, CancellationToken token)
    {
        var replacements = new Dictionary<int, byte[]>();
        var resources = new Dictionary<int, byte[]>();
        await using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            foreach (var entry in raw.Entries.Where(e => e.TypeId == UnitTypeId && options.RepairUnits
                || e.TypeId == MaterialTypeId && options.MigrateMaterials))
            {
                if (entry.DataSize > MaxUnitBytes) { plan.BlockingReasons.Add("Unit/材质资源超过读取上限。"); continue; }
                resources[entry.Position] = await ReadAtAsync(input, entry.DataOffset, (int)entry.DataSize, token);
            }
        var characterPack = options.MigrateMaterials && raw.Entries.Any(entry => entry.TypeId == MaterialTypeId
            && entry.DataSize <= MaxUnitBytes && entry.DataSize >= 0x88
            && CompatibilityResourceTransforms.TryGetLegacyMaterialParentMigration(resources[entry.Position], out _, out _));
        var decisions = new Dictionary<int, (GameUnitReference Reference, byte[] Data, bool Custom)>();
        foreach (var entry in raw.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.DataSize > MaxUnitBytes) { if (entry.TypeId is UnitTypeId or MaterialTypeId) plan.BlockingReasons.Add("Unit/材质资源超过读取上限。"); continue; }
            if (entry.TypeId == UnitTypeId && options.RepairUnits)
            {
                var unit = resources[entry.Position];
                var reference = references?.Units.GetValueOrDefault(entry.ResourceId);
                if (reference?.Status != GameUnitReferenceStatus.Found || reference.UnitData is null)
                {
                    var reason = reference?.Status switch
                    {
                        GameUnitReferenceStatus.Ambiguous => "游戏参考的版本或 LOD 存在歧义",
                        GameUnitReferenceStatus.Error => $"游戏参考读取失败：{reference.ErrorMessage}",
                        _ => "游戏归档中未找到该 Unit"
                    };
                    plan.Warnings.Add($"Unit {entry.ResourceId:x16} 缺少唯一完整游戏参考，未修改。{reason}。");
                    continue;
                }
                if (reference.Version != ModernUnitVersion)
                { plan.BlockingReasons.Add($"Unit {entry.ResourceId:x16}: 目标游戏版本没有已验证的迁移规则。"); continue; }
                var originalVersion = U32(unit, 0x2c);
                if (originalVersion > ModernUnitVersion)
                { plan.BlockingReasons.Add($"Unit {entry.ResourceId:x16}: 未知源版本，不能按旧版规则降级。"); continue; }
                // 自动修复只迁移版本不匹配的 Unit，避免覆写当前版本作者自定义的 LOD。
                if (originalVersion == reference.Version && options.LodStrategy == CompatibilityLodStrategy.Automatic) continue;
                var mesh = ReadMeshIds(unit);
                var meshDiffers = mesh.Length > 0 && reference.MeshIds.Length > 0 && !mesh.AsSpan().SequenceEqual(reference.MeshIds);
                // 自动策略保留有自定义网格证据的 Unit；不把同包其他 Unit 一并推定为自定义。
                var custom = meshDiffers || entry.GpuSize >= 5 * 1024 * 1024
                    && (reference.GpuSize == 0 || entry.GpuSize / (double)reference.GpuSize >= 8);
                decisions[entry.Position] = (reference, unit, custom);
            }
            else if (entry.TypeId == MaterialTypeId && options.MigrateMaterials)
            {
                var material = resources[entry.Position].AsSpan();
                byte[]? updated = null;
                if (CompatibilityResourceTransforms.TryGetLegacyMaterialParentMigration(material, out _, out var parent))
                { updated = material.ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(0x18), parent); }
                else if (CompatibilityResourceTransforms.TryBuildLegacyEmissiveMaterialMigration(material, out var emissive)) updated = emissive;
                if (updated is null) continue;
                replacements[entry.Position] = updated;
                plan.Actions.Add(new CompatibilityContentAction { Kind = "MaterialMigration", RelativePath = Path.GetRelativePath(root, original),
                    ResourceId = entry.ResourceId, OldSize = entry.DataSize, NewSize = updated.Length, Description = "按已知材质语义及参数布局迁移。" });
            }
        }
        foreach (var entry in raw.Entries.Where(e => decisions.ContainsKey(e.Position)))
        {
            token.ThrowIfCancellationRequested();
            var (reference, unit, custom) = decisions[entry.Position];
            var strategy = options.LodStrategy switch
            {
                CompatibilityLodStrategy.Automatic => custom ? CompatibilityLodStrategy.PreserveMod : CompatibilityLodStrategy.UseGameReference,
                CompatibilityLodStrategy.Mixed => options.PreserveModLodUnitIds.Contains(entry.ResourceId) ? CompatibilityLodStrategy.PreserveMod : CompatibilityLodStrategy.UseGameReference,
                _ => options.LodStrategy
            };
            if (characterPack && U32(unit, 0x2c) == 1 && reference.Version == ModernUnitVersion)
            {
                strategy = CompatibilityLodStrategy.UseGameReference;
                plan.Warnings.Add($"Unit {entry.ResourceId:x16}: 旧角色材质包需要匹配游戏 LOD。");
            }
            if (U32(unit, 0x30) == 0 && U32(unit, 0x34) == 0 && reference.LodGroupData.Length == 0)
            {
                if (entry.GpuSize != 0) { plan.BlockingReasons.Add("有 GPU 数据的 Unit 缺少 LOD，无法迁移。"); continue; }
                var updated = unit.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(0x2c), reference.Version!.Value);
                if (!updated.AsSpan().SequenceEqual(unit)) replacements[entry.Position] = updated;
            }
            else if (CompatibilityResourceTransforms.TryBuildUpdatedUnitData(unit, reference, strategy, out var updated, out var error))
            { if (!updated.AsSpan().SequenceEqual(unit)) replacements[entry.Position] = updated; }
            else { plan.BlockingReasons.Add($"Unit {entry.ResourceId:x16}: {error}"); continue; }
            if (replacements.TryGetValue(entry.Position, out var replacement))
                plan.Actions.Add(new CompatibilityContentAction { Kind = "UnitMigration", RelativePath = Path.GetRelativePath(root, original),
                    ResourceId = entry.ResourceId, OldVersion = U32(unit, 0x2c), NewVersion = reference.Version,
                    LodStrategy = strategy, OldSize = entry.DataSize, NewSize = replacement.Length,
                    Description = strategy == CompatibilityLodStrategy.PreserveMod ? "保留模组 LOD 并迁移版本/Layout。" : "迁移版本/Layout 并替换游戏 LOD。" });
        }
        if (replacements.Count == 0 || plan.BlockingReasons.Count > 0) return null;
        // 只有发现实际迁移动作才读取完整主补丁；正常检查/无变化预览不复制主补丁。
        if (new FileInfo(inputPath).Length > MaxRebuildBytes) throw new InvalidDataException("需要重建的主补丁超过 256 MiB。");
        var data = await File.ReadAllBytesAsync(inputPath, token);
        var originalHash = Convert.ToHexString(SHA256.HashData(data));
        foreach (var entry in raw.Entries.Where(e => resources.ContainsKey(e.Position)))
            if (!data.AsSpan((int)entry.DataOffset, (int)entry.DataSize).SequenceEqual(resources[entry.Position]))
                throw new InvalidDataException("主资源在生成修复计划期间发生变化。");
        var length = checked(data.LongLength + raw.Entries.Where(e => replacements.ContainsKey(e.Position))
            .Sum(e => replacements[e.Position].LongLength - e.DataSize));
        if (length > MaxRebuildBytes) throw new InvalidDataException("重建后的主补丁超过 256 MiB。");
        using var output = new MemoryStream((int)length);
        var cursor = 0;
        var updatedOffsets = new Dictionary<int, ulong>();
        foreach (var entry in raw.Entries.Where(e => e.DataSize > 0).OrderBy(e => e.DataOffset))
        {
            token.ThrowIfCancellationRequested();
            var start = checked((int)entry.DataOffset);
            output.Write(data, cursor, start - cursor);
            updatedOffsets[entry.Position] = (ulong)output.Position;
            if (replacements.TryGetValue(entry.Position, out var replacement)) output.Write(replacement);
            else output.Write(data, start, (int)entry.DataSize);
            cursor = checked(start + (int)entry.DataSize);
        }
        output.Write(data, cursor, data.Length - cursor);
        var rebuilt = output.GetBuffer();
        foreach (var entry in raw.Entries)
        {
            if (updatedOffsets.TryGetValue(entry.Position, out var offset)) BinaryPrimitives.WriteUInt64LittleEndian(rebuilt.AsSpan((int)entry.TableOffset + 16), offset);
            if (replacements.TryGetValue(entry.Position, out var replacement)) BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan((int)entry.TableOffset + 56), (uint)replacement.Length);
        }
        await File.WriteAllBytesAsync(temporary, rebuilt.AsMemory(0, (int)output.Length), token);
        return originalHash;
    }

    private static async Task<string> ShaAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
}
