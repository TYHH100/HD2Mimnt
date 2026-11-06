using System.Globalization;
using System.Text;
using HD2Mimnt.ModelConversion.Gmod.Physics;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;

namespace HD2Mimnt.ModelConversion.Gmod.Switches;

public sealed record IasSwitchPlan(
    IReadOnlyList<GmodSwitch> Switches,
    IReadOnlyList<GmodSwitchOption> Options,
    IReadOnlyList<IasUnitRoute> RigRoutes,
    IReadOnlyList<ulong> PublishedUnitIds,
    IasFreePartsDocument Differences,
    IReadOnlyList<GmodConversionIssue> Issues,
    IReadOnlyList<IasUnitRoute> CleanupRoutes,
    IReadOnlyList<ulong> CarriedNativeUnitIds);

/// <summary>
/// 把 Source 的 <c>$bodygroup</c> 与 skin family 变成 IAS v6 free-parts 开关。
/// 多个开关共用同一模型资源时（GMOD 常态）自动降级为单个组合开关，
/// 避免多个 part 争用同一 Native Unit 路由。
/// </summary>
public static class IasSwitchPlanner
{
    private static readonly IasPartSlot[] BodySlotOrder =
        [IasPartSlot.Torso, IasPartSlot.Torso_Armor, IasPartSlot.Hips, IasPartSlot.LeftArm,
            IasPartSlot.RightArm, IasPartSlot.LeftLeg, IasPartSlot.RightLeg];

    public static IasSwitchPlan Plan(SourceModel model, GmodConversionOptions options,
        IReadOnlySet<string>? allowedAxes = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = options.Binding;
        var issues = new List<GmodConversionIssue>();
        var fixedChoices = new Dictionary<string, int>(StringComparer.Ordinal);
        var axes = new List<Axis>();

        foreach (var part in model.Mdl.BodyParts)
        {
            var key = $"bodygroup:{part.Index}";
            if (part.Models.Count <= 1 || (allowedAxes is not null && !allowedAxes.Contains(key)))
            {
                fixedChoices[key] = 0;
                continue;
            }
            var axisOptions = part.Models.Select((alternative, index) => new AxisOption(
                $"bg{part.Index}_{index}", Display(alternative.Name, $"Alternative {index}"),
                key, index)).ToArray();
            axes.Add(new Axis($"bg{part.Index}", Display(part.Name, $"Body group {part.Index}"),
                GmodSwitchKind.BodyGroup, part.Index, axisOptions));
        }

        var skinCount = Math.Max(1, model.Mdl.SkinFamilies.Count);
        if (skinCount > 1 && options.IncludeSkinSwitch && (allowedAxes is null || allowedAxes.Contains("skin")))
        {
            axes.Add(new Axis("skin", "Skin", GmodSwitchKind.Skin, -1,
                Enumerable.Range(0, skinCount).Select(index => new AxisOption(
                    $"skin_{index}", $"Skin {index.ToString(CultureInfo.InvariantCulture)}", "skin", index)).ToArray()));
        }
        else
        {
            fixedChoices["skin"] = 0;
        }

        // 显式指定要发布的开关：其余 bodygroup 固定到第一个备选（几何包仍保留全部备选）。
        if (options.ExposedAxes is { Count: > 0 } exposedAxes)
        {
            var dropped = axes.Where(axis => !exposedAxes.Contains(axis.Key, StringComparer.Ordinal)).ToArray();
            foreach (var axis in dropped) fixedChoices[axis.Key] = 0;
            axes = axes.Where(axis => exposedAxes.Contains(axis.Key, StringComparer.Ordinal)).ToList();
            if (dropped.Length > 0)
            {
                issues.Add(new GmodConversionIssue("SwitchNotExposed", model.ModelPath,
                    $"Only {string.Join(", ", axes.Select(axis => axis.Key))} are published as IAS switches; " +
                    $"{string.Join(", ", dropped.Select(axis => axis.Key))} keep their first alternative. " +
                    "Every alternative remains in the HD2GEOM1 geometry bundle.", GmodIssueSeverity.Info));
            }
        }

        // 目标护甲的 Unit 闭包：必须同时属于每一份 equipment 记录，IAS 校验器才接受这些路由。
        var owned = binding.Equipment.Count == 0
            ? []
            : binding.Equipment.Select(row => row.NativeUnitIds.ToHashSet())
                .Aggregate((left, right) => left.Intersect(right).ToHashSet())
                .Where(id => id != 0).OrderBy(id => id).ToArray();
        if (owned.Length == 0)
            throw new IasFormatException("IAS equipment binding does not declare any shared native Unit ID.");

        // 载体 Unit：GMOD 的整身模型只能接管闭包里的一个槽位，其余槽位写成零面积占位，
        // 否则同一套整身网格会被游戏在每个槽位各画一遍。
        var native = options.CarrierUnitIds is { Count: > 0 } requested
            ? requested.Where(id => id != 0).Distinct().OrderBy(id => id).ToArray()
            : owned;
        if (native.Length == 0)
            throw new IasFormatException("The selected armour carrier Unit list is empty.");
        var outside = native.Where(id => !owned.Contains(id)).ToArray();
        if (outside.Length > 0)
            throw new IasFormatException($"Carrier Unit 0x{outside[0]:x16} is not owned by the target armour.");
        if (native.Length == owned.Length && owned.Length > 1 && options.CarrierUnitIds is { Count: > 0 })
            issues.Add(new GmodConversionIssue("EveryArmourUnitCarried", model.ModelPath,
                "Every Unit of the target armour is routed to this model, so the whole model would be drawn once " +
                "per armour slot. Select a single carrier Unit unless the model is split per armour part.",
                GmodIssueSeverity.Warning));

        var availableSlots = (options.PreferredSlots ?? (binding.Domain == IasContentDomain.Helmet
            ? [IasPartSlot.Head] : BodySlotOrder))
            .Where(slot => (slot == IasPartSlot.Head) == (binding.Domain == IasContentDomain.Helmet))
            .Distinct().ToArray();
        if (availableSlots.Length == 0)
            throw new IasFormatException("No IAS part slot is valid for the selected content domain.");

        var mode = options.SwitchMode == GmodSwitchMode.Auto
            ? axes.Count > 1 ? GmodSwitchMode.Composite : GmodSwitchMode.Independent
            : options.SwitchMode;

        // 组合开关的选项数是各开关备选数的乘积；超过 IAS 单 part 上限时按备选数从小到大保留，
        // 保证尽量多的开关能被玩家切换（被丢弃的开关固定到第一个备选并逐个提示）。
        if (mode == GmodSwitchMode.Composite && axes.Count > 1)
        {
            var combinationCount = axes.Aggregate(1L, (value, axis) => value * axis.Options.Count);
            if (combinationCount > options.MaxSwitchOptions)
            {
                var order = axes.Select((axis, index) => (axis.Key, index)).ToDictionary(entry => entry.Key, entry => entry.index);
                var kept = new List<Axis>();
                var dropped = new List<Axis>();
                long product = 1;
                foreach (var axis in axes.OrderBy(axis => axis.Options.Count).ThenBy(axis => order[axis.Key]))
                {
                    if (product * axis.Options.Count <= options.MaxSwitchOptions)
                    {
                        kept.Add(axis);
                        product *= axis.Options.Count;
                    }
                    else
                    {
                        dropped.Add(axis);
                    }
                }
                foreach (var axis in dropped) fixedChoices[axis.Key] = 0;
                axes = kept.OrderBy(axis => order[axis.Key]).ToList();
                issues.Add(new GmodConversionIssue("SwitchOptionsCapped", model.ModelPath,
                    $"Publishing all switches needs {combinationCount} combinations, above the IAS cap of " +
                    $"{options.MaxSwitchOptions} options per part. Exposed {axes.Count} switch(es) " +
                    $"({string.Join(", ", axes.Select(axis => axis.Key))}); " +
                    $"kept the first alternative for {string.Join(", ", dropped.Select(axis => $"{axis.Key}({axis.Options.Count})"))}. " +
                    "All alternatives remain in the HD2GEOM1 geometry bundle; choose the exposed switches explicitly to change this.",
                    GmodIssueSeverity.Warning));
            }
        }

        if (mode == GmodSwitchMode.Independent && axes.Count > availableSlots.Length)
            throw new IasFormatException($"IAS supports {availableSlots.Length} distinct slots in the {binding.Domain} domain, " +
                $"but the model has {axes.Count} switches; use composite switches.");
        if (mode == GmodSwitchMode.Composite && axes.Count > 1)
            issues.Add(new GmodConversionIssue("CompositeSwitch", model.ModelPath,
                $"{axes.Count} Source switches share one model resource, so they are published as one composite IAS switch " +
                "instead of overlapping independent native Unit routes.", GmodIssueSeverity.Info));

        var usedPublished = new HashSet<ulong>(owned);
        var switches = new List<GmodSwitch>();
        var parts = new List<IasFreePart>();
        var allRoutes = new List<IasUnitRoute>();
        var allOptions = new List<GmodSwitchOption>();

        if (mode == GmodSwitchMode.Composite || axes.Count == 0)
        {
            var combinations = Cartesian(axes, fixedChoices).ToArray();
            if (combinations.Length > options.MaxSwitchOptions)
                throw new IasFormatException($"The composite switch would publish {combinations.Length} options; " +
                    $"the IAS converter caps a single part at {options.MaxSwitchOptions} options. Split the model into separate IAS archives.");
            var slot = availableSlots[0];
            var optionRows = new List<IasFreeOption>();
            var switchOptions = new List<GmodSwitchOption>();
            for (var index = 0; index < combinations.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var choices = combinations[index];
                var optionId = $"opt{index.ToString(CultureInfo.InvariantCulture)}";
                var label = BuildLabel(axes, choices);
                var routes = native.Select(id => new IasUnitRoute(id, NextPublished(usedPublished,
                    options.ProjectId, binding.ArchiveId, "switch", optionId, id), true)).ToArray();
                optionRows.Add(new IasFreeOption(optionId, label, routes));
                switchOptions.Add(new GmodSwitchOption(optionId, label, choices));
                allRoutes.AddRange(routes);
            }
            allOptions.AddRange(switchOptions);
            var partId = axes.Count == 0 ? "default" : "composite";
            var partName = axes.Count == 0 ? "Default" : "Combined options";
            switches.Add(new GmodSwitch(partId, partName,
                axes.Count == 0 ? GmodSwitchKind.BodyGroup : GmodSwitchKind.Composite, slot, switchOptions));
            parts.Add(new IasFreePart(partId, slot, partName, optionRows));
        }
        else
        {
            var allocated = new HashSet<ulong>();
            for (var index = 0; index < axes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var axis = axes[index];
                var slot = ResolveSlot(options, axis, availableSlots, index);
                IReadOnlyList<ulong> axisNative = native;
                if (options.NativeUnitsByAxis is not null &&
                    options.NativeUnitsByAxis.TryGetValue(axis.Id, out var explicitNative))
                {
                    axisNative = explicitNative.Where(id => id != 0).Distinct().ToArray();
                    if (axisNative.Count == 0 || axisNative.Any(id => !native.Contains(id)))
                        throw new IasFormatException($"Native Unit allocation for switch {axis.Id} is empty or exceeds equipment ownership.");
                }
                else if (axes.Count > 1)
                {
                    throw new IasFormatException($"Independent switch {axis.Id} requires an explicit Native Unit allocation " +
                        "because IAS parts cannot share native Unit routes.");
                }
                if (axisNative.Any(id => !allocated.Add(id)))
                    throw new IasFormatException($"Independent switches overlap on Native Unit IDs: {axis.Id}.");

                var optionRows = new List<IasFreeOption>();
                var switchOptions = new List<GmodSwitchOption>();
                foreach (var axisOption in axis.Options)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var choices = new Dictionary<string, int>(fixedChoices, StringComparer.Ordinal);
                    foreach (var other in axes) choices[other.Key] = other.Options[0].Index;
                    choices[axis.Key] = axisOption.Index;
                    var routes = axisNative.Select(id => new IasUnitRoute(id, NextPublished(usedPublished,
                        options.ProjectId, binding.ArchiveId, axis.Id, axisOption.Id, id), true)).ToArray();
                    optionRows.Add(new IasFreeOption(axisOption.Id, axisOption.Display, routes));
                    switchOptions.Add(new GmodSwitchOption(axisOption.Id, axisOption.Display, choices));
                    allRoutes.AddRange(routes);
                }
                allOptions.AddRange(switchOptions);
                switches.Add(new GmodSwitch(axis.Id, axis.Display, axis.Kind, slot, switchOptions));
                parts.Add(new IasFreePart(axis.Id, slot, axis.Display, optionRows));
            }
        }

        var publishedIds = allRoutes.Select(route => route.PublishedUnitId).Distinct().ToArray();
        if (publishedIds.Length > 512)
            throw new IasFormatException($"The switch plan publishes {publishedIds.Length} Unit profiles; HD2IRG1 permits at most 512.");

        // 闭包里没有被接管的 Unit：写成零面积占位网格，原护甲才不会和模型一起显示。
        // 这些占位路由只进 isolation 清单（unit=native,alias），不参与 free-parts 选项，
        // 所以同一份占位 Unit 可以被所有开关选项共用。
        var cleanupRoutes = owned.Except(native).OrderBy(id => id)
            .Select(id => new IasUnitRoute(id, NextPublished(usedPublished, options.ProjectId,
                binding.ArchiveId, "cleanup", "slate", id), false))
            .ToArray();

        var differences = new IasFreePartsDocument(options.EffectiveManifestId, binding.Domain, binding.ArchiveId,
            binding.Equipment.Select(row => row.EquipmentKey).Distinct().ToArray(), parts.AsReadOnly());
        IasFreePartsCodec.Validate(differences, binding, cancellationToken);
        return new IasSwitchPlan(switches.AsReadOnly(), allOptions.AsReadOnly(), allRoutes.AsReadOnly(),
            publishedIds, differences, issues.AsReadOnly(), cleanupRoutes, native);
    }

    private static IasPartSlot ResolveSlot(GmodConversionOptions options, Axis axis,
        IasPartSlot[] available, int index)
    {
        if (options.BodyGroupSlots is not null && axis.Kind == GmodSwitchKind.BodyGroup &&
            options.BodyGroupSlots.TryGetValue(axis.SourceIndex, out var mapped))
        {
            if (!available.Contains(mapped))
                throw new IasFormatException($"Configured slot {mapped} is not valid for switch {axis.Id}.");
            return mapped;
        }
        return available[Math.Min(index, available.Length - 1)];
    }

    private static IEnumerable<Dictionary<string, int>> Cartesian(IReadOnlyList<Axis> axes,
        IReadOnlyDictionary<string, int> fixedChoices)
    {
        if (axes.Count == 0)
        {
            yield return new Dictionary<string, int>(fixedChoices, StringComparer.Ordinal);
            yield break;
        }
        var indices = new int[axes.Count];
        while (true)
        {
            var choices = new Dictionary<string, int>(fixedChoices, StringComparer.Ordinal);
            for (var i = 0; i < axes.Count; i++) choices[axes[i].Key] = axes[i].Options[indices[i]].Index;
            yield return choices;
            var position = axes.Count - 1;
            while (position >= 0)
            {
                indices[position]++;
                if (indices[position] < axes[position].Options.Count) break;
                indices[position] = 0;
                position--;
            }
            if (position < 0) yield break;
        }
    }

    private static string BuildLabel(IReadOnlyList<Axis> axes, IReadOnlyDictionary<string, int> choices)
    {
        if (axes.Count == 0) return "Default";
        var parts = axes.Select(axis =>
        {
            var index = choices[axis.Key];
            return axis.Options.FirstOrDefault(option => option.Index == index)?.Display ?? index.ToString(CultureInfo.InvariantCulture);
        });
        return Display(string.Join(" / ", parts), "Option");
    }

    private static string Display(string value, string fallback)
    {
        var cleaned = new string(value.Select(ch => char.IsControl(ch) || ch is ',' or '#' or ';' ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) return fallback;
        var bytes = Encoding.UTF8.GetByteCount(cleaned);
        if (bytes <= 127) return cleaned;
        var shortened = new StringBuilder();
        foreach (var ch in cleaned)
        {
            if (Encoding.UTF8.GetByteCount(shortened.ToString() + ch) > 120) break;
            shortened.Append(ch);
        }
        return shortened.ToString().TrimEnd();
    }

    private static ulong NextPublished(HashSet<ulong> used, string projectId, ulong archiveId,
        string partId, string optionId, ulong nativeId)
    {
        var seed = Hd2PhysCodec.Fnv1A64(Encoding.UTF8.GetBytes(
            $"{projectId}|{archiveId:x16}|{partId}|{optionId}|{nativeId:x16}"));
        var value = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        while (!used.Add(value)) value = value == ulong.MaxValue ? 1 : value + 1;
        return value;
    }

    private sealed record Axis(string Id, string Display, GmodSwitchKind Kind, int SourceIndex,
        IReadOnlyList<AxisOption> Options)
    {
        public string Key => Kind == GmodSwitchKind.Skin ? "skin" : $"bodygroup:{SourceIndex}";
    }
    private sealed record AxisOption(string Id, string Display, string Key, int Index);
}
