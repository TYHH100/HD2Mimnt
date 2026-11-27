using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasModelToIasRequest(
    string Seed,
    string Name,
    string Author,
    string Description,
    string ModPath,
    string GameDataRoot,
    string OutputDirectory,
    IReadOnlyList<ModelToIasBranch> Branches,
    string? ThumbnailPath = null,
    string? ManifestIconPath = null);

public sealed record IasModelToIasResult(
    string Name,
    string OutputDirectory,
    int SourceUnitCount,
    int AliasUnitCount,
    int ProfileCount,
    int ArchiveCount,
    int PartCount);

public readonly record struct IasModelToIasProgress(double Percent, string Stage, string? Message = null);

public static class IasModelToIasConverter
{
    private const long MaxTocBytes = 512L * 1024 * 1024;
    private static readonly Regex PatchName = new("\\A[0-9a-fA-F]{16}\\.patch_\\d+\\z", RegexOptions.Compiled);
    private static readonly string[] SourceNames =
    [
        "boss", "spine1", "spine2", "chest", "neck", "head",
        "r_clavicle", "r_shoulder", "r_elbow", "r_hand",
        "r_thumb_finger1", "r_thumb_finger2", "r_thumb_finger3",
        "r_index_finger1", "r_index_finger2", "r_index_finger3",
        "r_middle_finger1", "r_middle_finger2", "r_middle_finger3",
        "r_ring_finger1", "r_ring_finger2", "r_ring_finger3",
        "r_pinky_finger1", "r_pinky_finger2", "r_pinky_finger3",
        "r_hand_twist", "r_shoulder_twist",
        "l_clavicle", "l_shoulder", "l_elbow", "l_hand_twist", "l_hand",
        "l_thumb_finger1", "l_thumb_finger2", "l_thumb_finger3",
        "l_index_finger1", "l_index_finger2", "l_index_finger3",
        "l_middle_finger1", "l_middle_finger2", "l_middle_finger3",
        "l_ring_finger1", "l_ring_finger2", "l_ring_finger3",
        "l_pinky_finger1", "l_pinky_finger2", "l_pinky_finger3",
        "l_shoulder_twist", "hips", "l_thigh", "l_knee", "l_foot", "l_ball", "l_toe",
        "r_thigh", "r_knee", "r_foot", "r_ball", "r_toe",
        "support_mg", "attach_intelpad", "attach_samplepouch", "root"
    ];
    private static readonly string[] SlotOrder = ["Hips", "LeftArm", "RightArm", "Torso", "Torso_Armor", "LeftLeg", "RightLeg", "Head"];
    private static readonly IReadOnlyDictionary<string, string> SlotNames = new Dictionary<string, string>
    {
        ["Hips"] = "胯部", ["LeftArm"] = "左臂", ["RightArm"] = "右臂", ["Torso"] = "躯干",
        ["Torso_Armor"] = "胸甲", ["LeftLeg"] = "左腿", ["RightLeg"] = "右腿", ["Head"] = "头部"
    };

    public static async Task<IasModelToIasResult> BuildAsync(IasModelToIasRequest request,
        IProgress<IasModelToIasProgress>? progress, CancellationToken token)
    {
        IasBinary.Require(Directory.Exists(request.ModPath), "普通模型目录不存在");
        IasBinary.Require(Directory.Exists(request.GameDataRoot), "游戏 data 目录不存在");
        IasBinary.Require(request.Branches.Count > 0, "普通模型没有可转换的选项分支");

        var projectId = StableId(request.Seed, "project");
        var output = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(output);
        var runtimeDirectory = Path.Combine(output, "HD2IndependentRig");
        Directory.CreateDirectory(runtimeDirectory);
        var knownNames = KnownNames();

        progress?.Report(new(5, "扫描普通模型补丁"));
        using var game = new IasGameData(request.GameDataRoot);
        var scan = Scan(request, game, knownNames, token);
        IasBinary.Require(scan.Occurrences.Count > 0, "没有找到已收录的护甲/头盔 Unit，无法转换 IAS");
        progress?.Report(new(20, "规划私有 Unit 与局内差分", $"{scan.Occurrences.Count} 个来源 Unit"));

        var options = BuildOptions(scan, game, projectId, token);
        var plans = BuildPlans(options, projectId);
        var aliases = plans.SelectMany(plan => plan.Value.Routes).ToDictionary(route => route.Key, route => route.Value);
        IasBinary.Require(aliases.Count > 0, "没有生成 IAS 私有 Unit");
        progress?.Report(new(35, "生成 IAS 公共骨架与 Unit Profile", $"{aliases.Count} 个私有 Unit"));
        var sources = BuildSourceSkeleton(game, knownNames);
        var profiles = BuildProfiles(options, aliases, sources, knownNames);
        var rig = IasRigWriter.Write(sources, profiles);
        IasCompanionReader.ReadRig(rig);
        var rigName = projectId + ".rigbin";
        await File.WriteAllBytesAsync(Path.Combine(runtimeDirectory, rigName), rig, token);

        progress?.Report(new(55, "写出差分与隔离清单"));
        var runtimeMembers = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [rigName] = rig
        };
        foreach (var plan in plans.Values)
        {
            var difference = DifferenceText(plan, projectId);
            var isolation = IsolationText(plan, projectId);
            runtimeMembers.Add(difference.Name, Encoding.UTF8.GetBytes(difference.Text));
            runtimeMembers.Add(isolation.Name, Encoding.UTF8.GetBytes(isolation.Text));
            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, difference.Name), difference.Text,
                new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory, isolation.Name), isolation.Text,
                new UTF8Encoding(false), token);
        }

        progress?.Report(new(70, "写出私有模型补丁"));
        var archiveNames = scan.Occurrences.Select(row => row.Owner.Archive).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var archive in archiveNames)
        {
            token.ThrowIfCancellationRequested();
            var entries = new Dictionary<(ulong Type, ulong Id), IasResource>();
            foreach (var route in aliases.Where(pair => options.Single(option => option.Id == pair.Key.OptionId).Owner.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase)))
                entries[(IasResourceTypes.Unit, route.Value)] = options.Single(option => option.Id == route.Key.OptionId).Resources[route.Key.NativeId];
            var expectedAliases = aliases.Count(pair => options.Single(option => option.Id == pair.Key.OptionId).Owner.Archive.Equals(archive, StringComparison.OrdinalIgnoreCase));
            IasBinary.Require(entries.Count == expectedAliases, $"归档 {archive} 的私有 Unit 数量与路由不一致");
            IasBinary.Require(!entries.Keys.Any(key => scan.Occurrences.Any(row => row.NativeId == key.Id)), $"归档 {archive} 的私有 Unit 与原版 ID 冲突");
            var nativePackage = game.Resource("9ba626afa44a3aa3", IasResourceTypes.Package, ulong.Parse(archive, NumberStyles.HexNumber));
            var packageToc = AppendPackageDependencies(nativePackage.Toc, entries.Keys);
            entries[(IasResourceTypes.Package, ulong.Parse(archive, NumberStyles.HexNumber))] =
                new IasResource(packageToc, IasPayload.Empty, IasPayload.Empty);
            WriteArchive(output, archive + ".patch_0", entries, token);
        }
        if (scan.Materials.Count > 0)
            WriteArchive(output, "9ba626afa44a3aa3.patch_0", scan.Materials, token);

        progress?.Report(new(88, "回读校验 IAS 产物"));
        var routes = IasRoutes.Read(runtimeMembers);
        IasBinary.Require(routes.Published.ToHashSet(StringComparer.Ordinal).SetEquals(aliases.Values.Select(value => value.ToString("x16")).ToHashSet(StringComparer.Ordinal)),
            "IAS 路由与私有 Unit 集合不一致");
        IasBinary.Require(profiles.Select(profile => profile.UnitId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(aliases.Values.Select(value => value.ToString("x16")).ToHashSet(StringComparer.Ordinal)), "IAS Profile 与私有 Unit 集合不一致");
        foreach (var (key, alias) in aliases)
        {
            var option = options.Single(item => item.Id == key.OptionId);
            _ = new IasUnit(alias.ToString("x16", CultureInfo.InvariantCulture), option.Resources[key.NativeId],
                knownNames, allowUnnamed: true);
        }

        var manifestIcon = await CopyIconAsync(request, output, token);
        var guid = StableGuid(request.Seed, "model-to-ias");
        var manifest = new
        {
            Version = 1,
            Guid = guid.ToString("D"),
            Name = request.Name,
            Author = request.Author,
            Description = request.Description,
            IconPath = manifestIcon
        };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token);
        var summary = new
        {
            schema = "HD2MimntModelToIas1",
            project_id = projectId,
            source_units = scan.Occurrences.Count,
            alias_units = aliases.Count,
            profiles = profiles.Count,
            archives = archiveNames,
            parts = plans.Values.Sum(plan => plan.Parts.Count),
            rig_file = rigName,
            runtime_files = runtimeMembers.Keys.Order(StringComparer.Ordinal).ToArray()
        };
        await File.WriteAllTextAsync(Path.Combine(output, ".hd2mimnt-model-to-ias.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), token);
        progress?.Report(new(100, "转换完成", request.Name));
        return new(request.Name, output, scan.Occurrences.Count, aliases.Count, profiles.Count,
            archiveNames.Count, plans.Values.Sum(plan => plan.Parts.Count));
    }

    public static int CountRecognizedUnits(string modPath, IReadOnlyList<ModelToIasBranch> branches)
    {
        var count = 0;
        foreach (var branch in branches)
        foreach (var relative in branch.Directories)
        {
            var directory = Path.GetFullPath(Path.Combine(modPath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!directory.Equals(Path.GetFullPath(modPath), StringComparison.OrdinalIgnoreCase)
                && !PathGuard.IsInsideRoot(modPath, directory)) continue;
            if (!Directory.Exists(directory)) continue;
            foreach (var patch in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Where(file => PatchName.IsMatch(Path.GetFileName(file))))
            {
                try
                {
                    count += IasArchive.ReadRows(ReadToc(patch), replaceDuplicates: true).Keys.Count(key =>
                        key.Type == IasResourceTypes.Unit && IasModelToIasCatalog.TryGet(key.Id, out _));
                }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // 单个损坏补丁不应阻止其它模组进入候选列表。
                }
            }
        }
        return count;
    }

    private static ScanResult Scan(IasModelToIasRequest request, IasGameData game,
        IReadOnlyDictionary<uint, string> knownNames, CancellationToken token)
    {
        var occurrences = new List<Occurrence>();
        var materials = new Dictionary<(ulong Type, ulong Id), IasResource>();
        for (var branchIndex = 0; branchIndex < request.Branches.Count; branchIndex++)
        {
            token.ThrowIfCancellationRequested();
            var branch = request.Branches[branchIndex];
            foreach (var relative in branch.Directories)
            {
                var directory = Path.GetFullPath(Path.Combine(request.ModPath, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!directory.Equals(Path.GetFullPath(request.ModPath), StringComparison.OrdinalIgnoreCase)
                    && !PathGuard.IsInsideRoot(request.ModPath, directory))
                    throw new InvalidDataException("模型转换选项目录越界：" + relative);
                if (!Directory.Exists(directory)) continue;
                foreach (var patch in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                             .Where(file => PatchName.IsMatch(Path.GetFileName(file)))
                             .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var toc = ReadToc(patch);
                    var gpuPath = patch + ".gpu_resources";
                    var streamPath = patch + ".stream";
                    var gpu = File.Exists(gpuPath) ? IasPayload.From(gpuPath, 0, new FileInfo(gpuPath).Length) : IasPayload.Empty;
                    var stream = File.Exists(streamPath) ? IasPayload.From(streamPath, 0, new FileInfo(streamPath).Length) : IasPayload.Empty;
                    foreach (var (key, resource) in IasArchive.ReadEntries(toc, gpu, stream, replaceDuplicates: true))
                    {
                        token.ThrowIfCancellationRequested();
                        if (key.Type != IasResourceTypes.Unit)
                        {
                            if (key.Type is not (IasResourceTypes.Material or IasResourceTypes.Texture)) continue;
                            if (materials.TryGetValue(key, out var existing)
                                && IasHash.ResourceFingerprint(existing) != IasHash.ResourceFingerprint(resource))
                                throw new InvalidDataException($"普通模型包含冲突的材质/贴图 {key.Id:x16}，无法安全合并。");
                            materials[key] = resource;
                            continue;
                        }
                        if (!IasModelToIasCatalog.TryGet(key.Id, out var target))
                            continue;
                        IasBinary.Require(resource.Toc.Length >= 16 && BinaryPrimitives.ReadUInt64LittleEndian(resource.Toc.AsSpan(8)) == 0,
                            $"Unit {key.Id:x16} 引用外部 Bones；当前转换器只支持自带完整 TransformInfo 的模型");
                        var unit = new IasUnit(key.Id.ToString("x16"), resource, knownNames, allowUnnamed: true);
                        var slot = ClassifySlot(unit, target.Slot);
                        var profile = ClassifyProfile(unit, target.Profile);
                        var owner = ResolveOwner(game, key.Id, target, token);
                        var occurrenceId = StableId(request.Seed, "occurrence", branchIndex.ToString(CultureInfo.InvariantCulture), relative, key.Id.ToString("x16"));
                        occurrences.Add(new Occurrence(occurrenceId, branch.Name, relative, key.Id, slot,
                            profile, owner, new Dictionary<ulong, IasResource> { [key.Id] = resource }));
                    }
                }
            }
        }
        return new(occurrences, materials);
    }

    private static string ClassifySlot(IasUnit unit, string fallback)
    {
        var slot = unit.Customization.GetValueOrDefault("slot", "");
        var piece = unit.Customization.GetValueOrDefault("piece_type", "");
        var mapped = slot switch
        {
            "HelldiverCustomizationSlot_Hip" => "Hips",
            "HelldiverCustomizationSlot_LeftArm" => "LeftArm",
            "HelldiverCustomizationSlot_RightArm" => "RightArm",
            "HelldiverCustomizationSlot_LeftLeg" => "LeftLeg",
            "HelldiverCustomizationSlot_RightLeg" => "RightLeg",
            "HelldiverCustomizationSlot_Helmet" => "Head",
            "HelldiverCustomizationSlot_LeftShoulder" or "HelldiverCustomizationSlot_RightShoulder" => "Torso_Armor",
            "HelldiverCustomizationSlot_Torso" => piece.EndsWith("_Armor", StringComparison.Ordinal) ? "Torso_Armor" : "Torso",
            _ => fallback
        };
        IasBinary.Require(SlotOrder.Contains(mapped), $"Unit {unit.Id} 的语义部位无法识别（slot={(string.IsNullOrEmpty(slot) ? "empty" : slot)}）");
        return mapped;
    }

    private static string ClassifyProfile(IasUnit unit, string fallback)
    {
        var body = unit.Customization.GetValueOrDefault("body_type", "");
        if (body.EndsWith("_Slim", StringComparison.Ordinal)) return "Slim";
        if (body.EndsWith("_Stocky", StringComparison.Ordinal)) return "Stocky";
        if (body.EndsWith("_Any", StringComparison.Ordinal)) return "Shared";
        return fallback;
    }

    private static IasModelOwner ResolveOwner(IasGameData game, ulong unitId, IasModelTarget target,
        CancellationToken token)
    {
        var archives = game.FindResourceArchives(IasResourceTypes.Unit, unitId,
            target.Owners.Select(owner => owner.Archive).ToList(), token);
        var matches = target.Owners.Where(owner => archives.Contains(owner.Archive, StringComparer.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        if (archives.Count == 0 && target.Owners.Count == 1) return target.Owners[0];
        throw new InvalidDataException($"Unit {unitId:x16} 的装备归属不明确（候选 {matches.Count} 个），无法安全生成 IAS 路由。");
    }

    private static List<OptionSource> BuildOptions(ScanResult scan, IasGameData game, string projectId,
        CancellationToken token)
    {
        var grouped = scan.Occurrences.GroupBy(row => (row.Owner.Archive, row.Owner.Equipment, row.Owner.Domain,
            row.Slot, row.BranchName, row.RelativeDirectory), OptionGroupComparer.Instance);
        var options = grouped.Select(group =>
        {
            var first = group.First();
            var id = StableId(projectId, "option", first.Owner.Archive, first.Owner.Equipment.ToString("x8"),
                first.Owner.Domain, first.Slot, first.BranchName, first.RelativeDirectory);
            var resources = new Dictionary<ulong, IasResource>();
            foreach (var row in group)
            {
                if (resources.TryGetValue(row.NativeId, out var existing)
                    && IasHash.ResourceFingerprint(existing) != IasHash.ResourceFingerprint(row.Resources[row.NativeId]))
                    throw new InvalidDataException($"选项 {first.BranchName} 的 Unit {row.NativeId:x16} 存在冲突版本。");
                resources[row.NativeId] = row.Resources[row.NativeId];
            }
            return new OptionSource(id, first.BranchName, first.RelativeDirectory, first.Slot, first.Owner, resources,
                new HashSet<ulong>());
        }).ToList();
        var result = new List<OptionSource>();
        foreach (var slotGroup in options.GroupBy(option => (option.Owner.Archive, option.Owner.Equipment,
                     option.Owner.Domain, option.Slot), SlotGroupComparer.Instance).ToList())
        {
            var rows = slotGroup.ToList();
            var baseRow = rows.FirstOrDefault();
            var nativeIds = rows.SelectMany(row => row.Resources.Keys).Distinct().Order().ToList();
            var first = rows[0];
            var stockResources = new Dictionary<ulong, IasResource>();
            foreach (var native in nativeIds)
            {
                token.ThrowIfCancellationRequested();
                var resource = game.Resource(first.Owner.Archive, IasResourceTypes.Unit, native);
                IasBinary.Require(resource.Toc.Length >= 16
                                  && BinaryPrimitives.ReadUInt64LittleEndian(resource.Toc.AsSpan(8)) == 0,
                    $"原版 Unit {native:x16} 引用外部 Bones，无法生成关闭选项");
                stockResources[native] = resource;
            }
            var stock = new OptionSource(StableId(projectId, "stock-option", first.Owner.Archive,
                first.Owner.Equipment.ToString("x8"), first.Owner.Domain, first.Slot),
                "关闭", "", first.Slot, first.Owner, stockResources, stockResources.Keys.ToHashSet());
            var coverage = nativeIds.Select(native => new
            {
                Native = native,
                Signature = string.Join(',', rows.Where(row => row.Resources.ContainsKey(native)).Select(row => row.Id))
            }).ToList();
            var choiceSets = new List<List<Choice>>();
            foreach (var partition in coverage.GroupBy(item => item.Signature, StringComparer.Ordinal))
            {
                var partitionNativeIds = partition.Select(item => item.Native).Order().ToArray();
                var ids = partition.Key.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                var participating = rows.Where(row => ids.Contains(row.Id)).ToList();
                var groupName = string.Join('/', participating
                    .Where(row => baseRow is null || row.Id != baseRow.Id)
                    .Select(row => row.BranchName).Distinct(StringComparer.Ordinal));
                if (string.IsNullOrEmpty(groupName)) groupName = SlotNames.GetValueOrDefault(first.Slot, first.Slot);
                var choices = new List<Choice>();
                if (baseRow is not null && participating.Any(row => row.Id == baseRow.Id))
                {
                    choices.AddRange(participating.Where(row => row.Id != baseRow.Id)
                        .Select(row => new Choice(row, partitionNativeIds, groupName)));
                    choices.Add(new Choice(baseRow, partitionNativeIds, groupName));
                }
                else
                {
                    choices.AddRange(participating.Select(row => new Choice(row, partitionNativeIds, groupName)));
                }
                choiceSets.Add(choices);
            }

            var combinationCount = choiceSets.Aggregate(1, (total, choices) => checked(total * choices.Count));
            IasBinary.Require(combinationCount <= 64, $"部位 {first.Slot} 的独立选项组合过多（{combinationCount}），无法安全生成局内切换");

            void Walk(int index, List<Choice> selected)
            {
                if (index < choiceSets.Count)
                {
                    foreach (var choice in choiceSets[index])
                    {
                        selected.Add(choice);
                        Walk(index + 1, selected);
                        selected.RemoveAt(selected.Count - 1);
                    }
                    return;
                }
                var resources = new Dictionary<ulong, IasResource>();
                var stockNativeIds = new HashSet<ulong>();
                foreach (var choice in selected)
                foreach (var native in choice.NativeIds)
                {
                    resources[native] = choice.Row.Resources[native];
                    if (choice.Row.StockNativeIds.Contains(native)) stockNativeIds.Add(native);
                }
                var states = selected
                    .Where(choice => baseRow is null || choice.Row.Id != baseRow.Id)
                    .Select(choice => choice.Row.BranchName).ToList();
                var label = states.Count == 0 ? "本体" : string.Join('+', states);
                var id = StableId(projectId, "combination", first.Owner.Archive,
                    first.Owner.Equipment.ToString("x8"), first.Slot,
                    string.Join(',', selected.Select(choice => choice.Row.Id)));
                var relative = string.Join('|', selected.Select(choice => choice.Row.RelativeDirectory)
                    .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.OrdinalIgnoreCase));
                result.Add(new OptionSource(id, label, relative, first.Slot, first.Owner, resources, stockNativeIds));
            }
            Walk(0, []);
            result.Add(stock);
        }
        return result;
    }

    private static Dictionary<string, Plan> BuildPlans(IReadOnlyList<OptionSource> sources, string projectId)
    {
        var groups = sources.GroupBy(row => (row.Owner.Archive, row.Owner.Equipment, row.Owner.Domain), StringTupleComparer.Instance);
        var result = new Dictionary<string, Plan>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var key = group.Key;
            var planId = StableId(projectId, "plan", key.Archive, key.Equipment.ToString("x8"), key.Domain);
            var parts = new List<PlanPart>();
            var routes = new Dictionary<RouteKey, ulong>();
            foreach (var slot in SlotOrder)
            {
                var rows = group.Where(row => row.Slot == slot).ToList();
                if (rows.Count == 0) continue;
                var nativeIds = rows.SelectMany(row => row.Resources.Keys).Distinct().Order().ToList();
                var variants = rows.Select(row => row.BranchName)
                    .SelectMany(label => label.Split('+'))
                    .Where(label => label != "本体" && !label.EndsWith("关闭", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal).ToList();
                var slotName = SlotNames.GetValueOrDefault(slot, slot);
                var partName = variants.Count > 0 ? $"{slotName}（{string.Join('/', variants)}）" : slotName;
                var partId = Label("fp", projectId, key.Archive, key.Equipment.ToString("x8"), slot);
                var options = new List<PlanOption>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var optionIndex = 0; optionIndex < rows.Count; optionIndex++)
                {
                    var row = rows[optionIndex];
                    var display = row.BranchName;
                    if (!usedNames.Add(display)) display += " · " + row.RelativeDirectory;
                    var optionId = Label("fo", projectId, partId, row.Id);
                    var optionRoutes = new List<PlanRoute>();
                    foreach (var native in nativeIds)
                    {
                        var alias = StableUlong(projectId, "unit", row.Id, native.ToString("x16"));
                        routes.Add(new RouteKey(row.Id, native), alias);
                        optionRoutes.Add(new PlanRoute(native, alias));
                    }
                    options.Add(new PlanOption(optionId, display, optionIndex, optionRoutes));
                }
                parts.Add(new PlanPart(partId, partName, slot, options));
            }
            IasBinary.Require(parts.Count > 0, "IAS 计划没有部位");
            result.Add(planId, new Plan(planId, key.Archive, key.Equipment, key.Domain, parts, routes));
        }
        return result;
    }

    private static (string Name, string Text) DifferenceText(Plan plan, string projectId)
    {
        var manifestId = Label("hd2free", projectId, plan.Archive, plan.Equipment.ToString("x8"), plan.Domain);
        var lines = new List<string>
        {
            "manifest_version=6", $"manifest_id={manifestId}", "difference_logic=free_parts",
            $"domain={plan.Domain}", $"archive=0x{ulong.Parse(plan.Archive, NumberStyles.HexNumber):X16}",
            $"equipment=0x{plan.Equipment:X8}", ""
        };
        foreach (var part in plan.Parts)
        {
            lines.Add($"free_part={part.Id},{part.Slot},{part.Options[0].Id}");
            lines.Add($"free_part_display_name={part.Id},{part.Name}");
            foreach (var option in part.Options)
            {
                lines.Add($"free_option={part.Id},{option.Id},{option.Order}");
                lines.Add($"free_option_display_name={part.Id},{option.Id},{option.Name}");
                foreach (var route in option.Routes.OrderBy(route => route.NativeId))
                {
                    var defaultAlias = part.Options[0].Routes.Single(value => value.NativeId == route.NativeId).AliasId;
                    lines.Add($"free_route={part.Id},{option.Id},0x{route.NativeId:X16},0x{defaultAlias:X16},0x{route.AliasId:X16}");
                    lines.Add($"free_rig_profile={part.Id},{option.Id},0x{route.AliasId:X16}");
                }
            }
            lines.Add("");
        }
        return (manifestId + ".difference.ini", string.Join('\n', lines) + "\n");
    }

    private static (string Name, string Text) IsolationText(Plan plan, string projectId)
    {
        var manifestId = Label("owned", projectId, plan.Archive, plan.Equipment.ToString("x8"), plan.Domain, digits: 24);
        var lines = new List<string>
        {
            "isolation_version=1", $"isolation_id={manifestId}", $"domain={plan.Domain}",
            $"archive=0x{ulong.Parse(plan.Archive, NumberStyles.HexNumber):X16}", $"equipment=0x{plan.Equipment:X8}"
        };
        foreach (var part in plan.Parts)
        foreach (var route in part.Options[0].Routes.OrderBy(route => route.NativeId))
            lines.Add($"unit=0x{route.NativeId:X16},0x{route.AliasId:X16}");
        foreach (var alias in plan.Routes.Values.Distinct().Order())
            lines.Add($"rig_profile=0x{alias:X16}");
        return (manifestId + ".isolation.ini", string.Join('\n', lines) + "\n");
    }

    private static List<IasSourceBone> BuildSourceSkeleton(IasGameData game, IReadOnlyDictionary<uint, string> knownNames)
    {
        var avatar = game.Resource("18235e0c9ec0e636", IasResourceTypes.Unit, 0x4d1c334d294dfa97);
        var graph = IasUnit.ReadGraph(avatar.Toc, knownNames);
        var byHash = graph.ToDictionary(node => node.Hash);
        var selected = new List<IasSceneNode>();
        foreach (var name in SourceNames)
        {
            var hash = IasHash.Murmur32(name);
            IasBinary.Require(byHash.TryGetValue(hash, out var node), $"原版 Avatar 缺少公共骨 {name}");
            selected.Add(node!);
        }
        var indices = selected.Select((node, index) => (node.Hash, Index: index)).ToDictionary(pair => pair.Hash, pair => pair.Index);
        var selectedHashes = indices.Keys.ToHashSet();
        var sources = new List<IasSourceBone>();
        for (var index = 0; index < selected.Count; index++)
        {
            var node = selected[index];
            var parent = SourceNames[index] == "boss" ? 0 : node.Parent;
            while (parent != 0 && !selectedHashes.Contains(graph[parent].Hash)) parent = graph[parent].Parent;
            var parentIndex = parent == 0 ? -1 : indices[graph[parent].Hash];
            var local = parentIndex < 0 ? node.World : IasAffine.Product(IasAffine.Inverse(selected[parentIndex].World), node.World);
            sources.Add(new IasSourceBone(SourceNames[index], parentIndex, parentIndex < 0 ? 1U : 0U, local));
        }
        return sources;
    }

    private static List<IasRigProfile> BuildProfiles(IReadOnlyList<OptionSource> options, IReadOnlyDictionary<RouteKey, ulong> aliases,
        IReadOnlyList<IasSourceBone> sources, IReadOnlyDictionary<uint, string> knownNames)
    {
        var sourceIndices = sources.Select((source, index) => (source.Name, Index: index)).ToDictionary(pair => pair.Name, pair => pair.Index);
        var sourceHashes = sourceIndices.Keys.ToHashSet(StringComparer.Ordinal);
        var profiles = new List<IasRigProfile>();
        foreach (var route in aliases.OrderBy(pair => pair.Key.OptionId, StringComparer.Ordinal).ThenBy(pair => pair.Key.NativeId))
        {
            var option = options.Single(item => item.Id == route.Key.OptionId);
            var unit = new IasUnit(route.Key.NativeId.ToString("x16"), option.Resources[route.Key.NativeId], knownNames, allowUnnamed: true);
            var active = option.StockNativeIds.Contains(route.Key.NativeId) ? new HashSet<int>() : ActiveScenes(unit);
            var selected = unit.Graph.Where(node => sourceHashes.Contains(node.Name ?? "") || active.Contains(node.Index)).ToList();
            var selectedIndices = selected.Select(node => node.Index).ToHashSet();
            var ordered = selected.OrderBy(node => node.Index).ToList();
            var remap = ordered.Select((node, index) => (NodeIndex: node.Index, NewIndex: index))
                .ToDictionary(pair => pair.NodeIndex, pair => pair.NewIndex);
            var targets = new List<IasRigNode>();
            foreach (var node in ordered)
            {
                var parent = node.Parent;
                while (parent != 0 && !selectedIndices.Contains(parent)) parent = unit.Graph[parent].Parent;
                int parentIndex;
                if (parent == 0) parentIndex = -1;
                else if (!remap.TryGetValue(parent, out parentIndex))
                    throw new InvalidDataException($"Unit {unit.Id} 的父骨重映射失败：parent={parent}");
                var local = parentIndex < 0 ? node.World : IasAffine.Product(IasAffine.Inverse(ordered[parentIndex].World), node.World);
                var sourceName = node.Name is not null && sourceHashes.Contains(node.Name) ? node.Name : null;
                if (sourceName is not null)
                {
                    targets.Add(new IasRigNode(sourceName, parentIndex, sourceIndices[sourceName], node.Index, 1057, 1, local));
                }
                else
                {
                    IasBinary.Require(parentIndex >= 0, $"Unit {unit.Id} 的加权骨 {node.Hash:x8} 没有可继承父骨");
                    targets.Add(new IasRigNode($"hash_{node.Hash:x8}", parentIndex, -1, node.Index, 288, 1, local));
                }
            }
            IasBinary.Require(targets.Count(node => node.SourceIndex >= 0) >= 3, $"Unit {unit.Id} 的公共骨映射不足");
            profiles.Add(new IasRigProfile(route.Value.ToString("x16"), targets, unit.Graph.Count,
                0U, string.Concat(Enumerable.Repeat("00", 16)), 0, []));
        }
        return profiles;
    }

    private static HashSet<int> ActiveScenes(IasUnit unit)
    {
        var result = new HashSet<int>();
        foreach (var mesh in unit.Meshes.Where(mesh => mesh.Lod >= 0 && !mesh.Culling))
        {
            if (unit.Unskinned(mesh))
            {
                result.Add((int)mesh.Transform);
                continue;
            }
            foreach (var section in mesh.Sections)
            {
                var vertices = section.Faces.Where(face => face.A != face.B && face.A != face.C && face.B != face.C)
                    .SelectMany(face => new[] { face.A, face.B, face.C }).Distinct().ToList();
                foreach (var vertex in vertices)
                    foreach (var influence in unit.Influences(mesh, section, vertex)) result.Add(influence.Scene);
            }
        }
        return result;
    }

    private static byte[] ReadToc(string path)
    {
        var length = new FileInfo(path).Length;
        IasBinary.Require(length <= MaxTocBytes, $"模型索引文件过大：{Path.GetFileName(path)}");
        return File.ReadAllBytes(path);
    }

    private static byte[] AppendPackageDependencies(byte[] original, IEnumerable<(ulong Type, ulong Id)> dependencies)
    {
        IasBinary.Require(original.Length >= 16, "Package header");
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(8));
        IasBinary.Require(count >= 0 && original.Length == 16L + count * 16L, "Package dependency table");
        var existing = new HashSet<(ulong Type, ulong Id)>();
        for (var index = 0; index < count; index++)
            existing.Add((BinaryPrimitives.ReadUInt64LittleEndian(original.AsSpan(16 + index * 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(original.AsSpan(24 + index * 16))));
        var added = dependencies.Where(existing.Add).Distinct().OrderBy(pair => pair.Type).ThenBy(pair => pair.Id).ToList();
        var result = new byte[original.Length + added.Count * 16];
        original.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)(count + added.Count));
        for (var index = 0; index < added.Count; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(original.Length + index * 16), added[index].Type);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(original.Length + index * 16 + 8), added[index].Id);
        }
        return result;
    }

    private static void WriteArchive(string output, string baseName,
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries, CancellationToken token)
    {
        var tocPath = Path.Combine(output, baseName);
        var gpuPath = tocPath + ".gpu_resources";
        var streamPath = tocPath + ".stream";
        using (var toc = File.Create(tocPath))
        using (var gpu = File.Create(gpuPath))
        using (var stream = File.Create(streamPath))
            IasArchive.WriteTo(entries, toc, gpu, stream, token);
        IasArchive.VerifyReadback(tocPath, gpuPath, streamPath, entries);
    }

    private static Task<string?> CopyIconAsync(IasModelToIasRequest request, string output, CancellationToken token)
    {
        var candidates = new[] { request.ThumbnailPath, request.ManifestIconPath }.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        foreach (var candidate in candidates)
        {
            if (candidate is null || !File.Exists(candidate)) continue;
            if (string.Equals(candidate, request.ManifestIconPath, StringComparison.OrdinalIgnoreCase)
                && !PathGuard.IsInsideRoot(request.ModPath, candidate))
                throw new InvalidDataException("模组封面路径超出模组目录。");
            var extension = Path.GetExtension(candidate).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp") || !IsSupportedImage(candidate)) continue;
            var name = "icon" + extension;
            token.ThrowIfCancellationRequested();
            File.Copy(candidate, Path.Combine(output, name), true);
            return Task.FromResult<string?>(name);
        }
        return Task.FromResult<string?>(null);
    }

    private static bool IsSupportedImage(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read < 12) return false;
        return (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
            || (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            || (header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8));
    }

    private static IReadOnlyDictionary<uint, string> KnownNames()
    {
        var result = new Dictionary<uint, string>
        {
            [IasHash.Murmur32("StingrayEntityRoot")] = "StingrayEntityRoot"
        };
        foreach (var name in SourceNames) result[IasHash.Murmur32(name)] = name;
        return result;
    }

    private static string StableId(string seed, params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts.Prepend(seed)))))[..32].ToLowerInvariant();

    private static ulong StableUlong(string seed, params string[] parts)
    {
        var hex = StableId(seed, parts);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(hex));
        return value == 0 ? 1 : value;
    }

    private static Guid StableGuid(string seed, string kind)
    {
        var bytes = Convert.FromHexString(StableId(seed, kind));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static string Label(string prefix, string projectId, params string[] values)
        => prefix + "_" + StableId(projectId, values)[..12];

    private static string Label(string prefix, string projectId, string archive, string equipment, string domain, int digits = 16)
        => prefix + "_" + StableId(projectId, "manifest", archive, equipment, domain)[..digits];

    private sealed record Occurrence(string Id, string BranchName, string RelativeDirectory, ulong NativeId,
        string Slot, string Profile, IasModelOwner Owner, Dictionary<ulong, IasResource> Resources);
    private sealed record ScanResult(List<Occurrence> Occurrences, Dictionary<(ulong Type, ulong Id), IasResource> Materials);
    private sealed record OptionSource(string Id, string BranchName, string RelativeDirectory, string Slot,
        IasModelOwner Owner, Dictionary<ulong, IasResource> Resources, IReadOnlySet<ulong> StockNativeIds);
    private sealed record Choice(OptionSource Row, ulong[] NativeIds, string GroupName);
    private readonly record struct RouteKey(string OptionId, ulong NativeId);
    private sealed record PlanRoute(ulong NativeId, ulong AliasId);
    private sealed record PlanOption(string Id, string Name, int Order, IReadOnlyList<PlanRoute> Routes);
    private sealed record PlanPart(string Id, string Name, string Slot, IReadOnlyList<PlanOption> Options);
    private sealed record Plan(string Id, string Archive, uint Equipment, string Domain,
        IReadOnlyList<PlanPart> Parts, IReadOnlyDictionary<RouteKey, ulong> Routes);
    private sealed class StringTupleComparer : IEqualityComparer<(string Archive, uint Equipment, string Domain)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Archive, uint Equipment, string Domain) x, (string Archive, uint Equipment, string Domain) y)
            => x.Archive.Equals(y.Archive, StringComparison.OrdinalIgnoreCase) && x.Equipment == y.Equipment
               && x.Domain.Equals(y.Domain, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Archive, uint Equipment, string Domain) obj)
            => HashCode.Combine(obj.Archive.ToUpperInvariant(), obj.Equipment, obj.Domain.ToUpperInvariant());
    }

    private sealed class SlotGroupComparer : IEqualityComparer<(string Archive, uint Equipment, string Domain, string Slot)>
    {
        public static readonly SlotGroupComparer Instance = new();
        public bool Equals((string Archive, uint Equipment, string Domain, string Slot) x,
            (string Archive, uint Equipment, string Domain, string Slot) y)
            => x.Archive.Equals(y.Archive, StringComparison.OrdinalIgnoreCase) && x.Equipment == y.Equipment
               && x.Domain.Equals(y.Domain, StringComparison.OrdinalIgnoreCase) && x.Slot == y.Slot;
        public int GetHashCode((string Archive, uint Equipment, string Domain, string Slot) obj)
            => HashCode.Combine(obj.Archive.ToUpperInvariant(), obj.Equipment, obj.Domain.ToUpperInvariant(), obj.Slot);
    }

    private sealed class OptionGroupComparer : IEqualityComparer<(string Archive, uint Equipment, string Domain,
        string Slot, string BranchName, string RelativeDirectory)>
    {
        public static readonly OptionGroupComparer Instance = new();
        public bool Equals((string Archive, uint Equipment, string Domain, string Slot, string BranchName, string RelativeDirectory) x,
            (string Archive, uint Equipment, string Domain, string Slot, string BranchName, string RelativeDirectory) y)
            => x.Archive.Equals(y.Archive, StringComparison.OrdinalIgnoreCase) && x.Equipment == y.Equipment
               && x.Domain.Equals(y.Domain, StringComparison.OrdinalIgnoreCase) && x.Slot == y.Slot
               && x.BranchName == y.BranchName
               && x.RelativeDirectory.Equals(y.RelativeDirectory, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Archive, uint Equipment, string Domain, string Slot, string BranchName, string RelativeDirectory) obj)
            => HashCode.Combine(obj.Archive.ToUpperInvariant(), obj.Equipment, obj.Domain.ToUpperInvariant(),
                obj.Slot, obj.BranchName, obj.RelativeDirectory.ToUpperInvariant());
    }
}
