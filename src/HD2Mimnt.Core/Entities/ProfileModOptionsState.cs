using System.Text.Json;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// 配置文件中某个模组的选项选择状态（按 ProfileMod 行持久化为 JSON）。
/// Legacy（v0）：LegacySelectedIndex = 互斥下拉的选中索引。
/// V1：OptionToggles = 每个选项的独立开关；OptionSelections = 普通互斥子选项的选中索引。
/// 二级项全部含三级叶子时视为分类，分类开关存 SubOptionToggles，分类内颜色存 SubOptionSelections。
/// 同一模组在不同配置文件中的选择互相独立。
/// </summary>
public class ProfileModOptionsState
{
    public int? LegacySelectedIndex { get; set; }

    public List<bool>? OptionToggles { get; set; }

    public List<int>? OptionSelections { get; set; }

    /// <summary>
    /// 二级分类开关：SubOptionToggles[i][j] = 选项 i 下子选项 j 是否启用。
    /// 仅当所有二级项都含三级叶子时启用多选；普通互斥子选项由 ResolveEffective 同步为单选。
    /// </summary>
    public List<List<bool>>? SubOptionToggles { get; set; }

    /// <summary>
    /// 嵌套子选项的选中索引：SubOptionSelections[i][j] = 选项 i 下子选项 j
    /// 选中的子-子选项索引（缺省 0，越界由 <c>ResolveEffective</c> 钳制）。
    /// 每个二级分类各自保存一个颜色索引，分类开关见 <see cref="SubOptionToggles"/>。
    /// </summary>
    public List<List<int>>? SubOptionSelections { get; set; }

    public bool IsEmpty => LegacySelectedIndex is null && OptionToggles is null && OptionSelections is null
        && SubOptionSelections is null && SubOptionToggles is null;

    public static ProfileModOptionsState FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ProfileModOptionsState();
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileModOptionsState>(json) ?? new ProfileModOptionsState();
        }
        catch (JsonException)
        {
            return new ProfileModOptionsState();
        }
    }

    /// <summary>空状态序列化为空字符串（数据库列默认值），避免存储无意义的 "{}"。</summary>
    public string ToJson() => IsEmpty ? string.Empty : JsonSerializer.Serialize(this);
}

public enum OptionDeployScope
{
    /// <summary>模组没有选项，或清单缺少文件映射：保持原有全量目录部署。</summary>
    FullDirectory,

    /// <summary>按给定相对目录列表收集文件部署（列表可以为空 = 选项全部关闭时部署 0 个文件）。</summary>
    DirectoryList,

    /// <summary>选项选择与当前清单不匹配（如清单更新后选项变化）且无法安全解释：整个模组跳过部署。</summary>
    InvalidSelection
}

/// <summary>
/// 部署范围决策结果
/// </summary>
public sealed class OptionDeployPlan
{
    public OptionDeployScope Scope { get; init; }

    /// <summary>Scope 为 DirectoryList 时的相对目录列表（相对模组根目录）。</summary>
    public IReadOnlyList<string> Directories { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 模组选项选择的归一化与部署展开规则。
/// 与旧版管理器语义对齐：v0 只部署选中互斥项对应的子目录；v1 部署所有启用选项 Include 目录。
/// 普通子选项互斥单选；嵌套分类可多选，每个分类部署其选中的一个叶子。清单未声明映射时回退全量部署。
/// </summary>
public static class ProfileModOptionResolver
{
    /// <summary>模组是否带选项定义（v0 字符串下拉或 v1 独立开关）。</summary>
    public static bool HasOptions(ModManifest? manifest)
    {
        if (manifest == null)
        {
            return false;
        }

        return manifest.ManifestVersion == ManifestVersion.V1
            ? manifest.Options is { Count: > 0 }
            : manifest.LegacyOptions is { Count: > 0 };
    }

    public static bool SupportsSubOptionMultiSelect(ModOption option) =>
        SupportsSubOptionMultiSelect(option.SubOptions);

    private static bool SupportsSubOptionMultiSelect(IReadOnlyList<ModSubOption>? subOptions) =>
        subOptions is { Count: > 0 } && subOptions.All(sub => sub.SubOptions is { Count: > 0 });

    /// <summary>
    /// 把存储的选择状态与当前清单对齐：缺失的补默认值（v1 开关缺省启用、子选项缺省选第 0 个、
    /// v0 缺省选第 0 项），清单更新后长度不符时截断/补齐，越界索引钳制回有效范围。
    /// </summary>
    public static ProfileModOptionsState ResolveEffective(ModManifest? manifest, string? storedJson)
    {
        var state = ProfileModOptionsState.FromJson(storedJson);
        if (!HasOptions(manifest) || manifest == null)
        {
            return new ProfileModOptionsState();
        }

        if (manifest.ManifestVersion == ManifestVersion.V1)
        {
            var options = manifest.Options!;
            var toggles = new List<bool>(options.Count);
            var selections = new List<int>(options.Count);
            var subSelections = new List<List<int>>(options.Count);
            var subToggles = new List<List<bool>>(options.Count);
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var toggle = i < state.OptionToggles?.Count
                    ? state.OptionToggles[i]
                    : option.DefaultEnabled ?? true;
                toggles.Add(toggle);

                var subs = option.SubOptions ?? new List<ModSubOption>();
                var selected = i < state.OptionSelections?.Count ? state.OptionSelections[i] : 0;
                selected = subs.Count > 0 ? Math.Clamp(selected, 0, subs.Count - 1) : 0;
                var multiSelect = SupportsSubOptionMultiSelect(option);
                var storedToggles = state.SubOptionToggles is not null && i < state.SubOptionToggles.Count
                    ? state.SubOptionToggles[i] : null;
                var toggleRow = new List<bool>(subs.Count);
                var selectionRow = new List<int>(subs.Count);
                for (var j = 0; j < subs.Count; j++)
                {
                    toggleRow.Add(multiSelect
                        ? storedToggles is null || j >= storedToggles.Count || storedToggles[j]
                        : j == selected);
                    var nestedCount = subs[j].SubOptions?.Count ?? 0;
                    var storedSub = state.SubOptionSelections is not null &&
                        i < state.SubOptionSelections.Count && j < state.SubOptionSelections[i]?.Count
                        ? state.SubOptionSelections[i]![j] : 0;
                    selectionRow.Add(nestedCount > 0 ? Math.Clamp(storedSub, 0, nestedCount - 1) : 0);
                }
                subToggles.Add(toggleRow);
                subSelections.Add(selectionRow);
                var firstEnabled = toggleRow.FindIndex(value => value);
                selections.Add(multiSelect && firstEnabled >= 0 ? firstEnabled : selected);
            }

            return new ProfileModOptionsState
            {
                OptionToggles = toggles,
                OptionSelections = selections,
                SubOptionSelections = subSelections.Any(row => row.Count > 0) ? subSelections : null,
                SubOptionToggles = subToggles.Any(row => row.Count > 0) ? subToggles : null
            };
        }

        // Legacy：互斥下拉只有一个选中索引
        var legacyOptions = manifest.LegacyOptions!;
        var index = state.LegacySelectedIndex ?? 0;
        return new ProfileModOptionsState
        {
            LegacySelectedIndex = index >= 0 && index < legacyOptions.Count ? index : 0
        };
    }

    /// <summary>
    /// 部署时的文件收集范围。父选项可以只负责分组，由子选项声明 Include；
    /// 无子选项的条目及子选项缺少映射时，回退根目录部署保持兼容。
    /// </summary>
    public static OptionDeployPlan ResolveDeployPlan(ModManifest? manifest, ProfileModOptionsState state)
    {
        if (!HasOptions(manifest) || manifest == null)
        {
            return new OptionDeployPlan { Scope = OptionDeployScope.FullDirectory };
        }

        if (manifest.ManifestVersion == ManifestVersion.V1)
        {
            var options = manifest.Options!;
            // Include == null 表示清单未声明映射；空列表是显式的“无文件占位”，两者语义不同。
            // 判定递归到叶子：节点可映射 = 自身声明了 Include 或可继续下钻（有嵌套子选项）。
            // 任一层出现不可映射节点（Include null 且无嵌套）→ 整模组回退全量部署。
            if (options.Any(o => !IsMappable(o.Include, o.SubOptions)))
            {
                return new OptionDeployPlan { Scope = OptionDeployScope.FullDirectory };
            }

            var toggles = state.OptionToggles ?? new List<bool>(options.Select(o => o.DefaultEnabled ?? true));
            var selections = state.OptionSelections ?? new List<int>(new int[options.Count]);
            var subSelections = state.SubOptionSelections;
            var subToggles = state.SubOptionToggles ?? options.Select((option, i) =>
            {
                var selected = i < selections.Count ? Math.Clamp(selections[i], 0,
                    Math.Max((option.SubOptions?.Count ?? 0) - 1, 0)) : 0;
                return (option.SubOptions ?? new List<ModSubOption>())
                    .Select((_, subIndex) => SupportsSubOptionMultiSelect(option) || subIndex == selected).ToList();
            }).ToList();
            var directories = new List<string>();
            for (var i = 0; i < options.Count; i++)
            {
                if (i >= toggles.Count || !toggles[i])
                {
                    continue;
                }

                AddDirectories(directories, options[i].Include, options[i].SubOptions, i, selections, subSelections, subToggles);
            }

            return new OptionDeployPlan { Scope = OptionDeployScope.DirectoryList, Directories = directories };
        }

        // Legacy：只部署选中互斥项对应的子目录；索引无效时整个模组跳过，
        // 不能回退全量（会把互斥选项的文件一起部署，生成冲突补丁）
        var legacyOptions = manifest.LegacyOptions!;
        var selectedIndex = state.LegacySelectedIndex ?? 0;
        if (selectedIndex < 0 || selectedIndex >= legacyOptions.Count)
        {
            return new OptionDeployPlan { Scope = OptionDeployScope.InvalidSelection };
        }

        return new OptionDeployPlan
        {
            Scope = OptionDeployScope.DirectoryList,
            Directories = new[] { legacyOptions[selectedIndex] }
        };
    }

    /// <summary>
    /// 节点（选项或子选项）可映射：自身声明了 Include，或可继续下钻到嵌套子选项。
    /// 递归到叶子：任一层出现不可映射节点（Include null 且无嵌套）→ 整模组回退全量部署。
    /// </summary>
    private static bool IsMappable(List<string>? include, List<ModSubOption>? subOptions)
        => include != null
           || (subOptions is { Count: > 0 } && subOptions.All(s => IsMappable(s.Include, s.SubOptions)));

    /// <summary>
    /// 收集节点自身 Include 与启用的子分支。普通子选项仍按 OptionSelections 单选；
    /// 分类多选按 SubOptionToggles 收集所有启用分类，每个分类再取 SubOptionSelections 指定的一个叶子。
    /// </summary>
    private static void AddDirectories(
        List<string> directories,
        List<string>? include,
        List<ModSubOption>? subOptions,
        int optionIndex,
        List<int> selections,
        List<List<int>>? subSelections,
        List<List<bool>>? subToggles)
    {
        if (include != null) directories.AddRange(include);
        if (subOptions is not { Count: > 0 }) return;

        IEnumerable<int> selectedIndices;
        if (SupportsSubOptionMultiSelect(subOptions))
        {
            var toggles = subToggles is not null && optionIndex < subToggles.Count
                ? subToggles[optionIndex] : null;
            selectedIndices = Enumerable.Range(0, subOptions.Count)
                .Where(index => toggles is null || index >= toggles.Count || toggles[index]);
        }
        else
        {
            var selected = optionIndex < selections.Count
                ? Math.Clamp(selections[optionIndex], 0, subOptions.Count - 1) : 0;
            selectedIndices = new[] { selected };
        }

        foreach (var subIndex in selectedIndices)
        {
            var nested = subOptions[subIndex];
            if (nested.Include != null) directories.AddRange(nested.Include);
            if (nested.SubOptions is not { Count: > 0 }) continue;
            var leafIndex = subSelections is not null && optionIndex < subSelections.Count &&
                subIndex < subSelections[optionIndex]?.Count
                ? Math.Clamp(subSelections[optionIndex]![subIndex], 0, nested.SubOptions.Count - 1) : 0;
            if (nested.SubOptions[leafIndex].Include is { } leafInclude)
                directories.AddRange(leafInclude);
        }
    }
}
