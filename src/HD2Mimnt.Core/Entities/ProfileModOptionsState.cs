using System.Text.Json;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// 配置文件中某个模组的选项选择状态（按 ProfileMod 行持久化为 JSON）。
/// Legacy（v0）：LegacySelectedIndex = 互斥下拉的选中索引。
/// V1：OptionToggles = 每个选项的独立开关；OptionSelections = 每个选项内部子选项的选中索引。
/// 同一模组在不同配置文件中的选择互相独立。
/// </summary>
public class ProfileModOptionsState
{
    public int? LegacySelectedIndex { get; set; }

    public List<bool>? OptionToggles { get; set; }

    public List<int>? OptionSelections { get; set; }

    /// <summary>
    /// 嵌套子选项的选中索引：SubOptionSelections[i][j] = 选项 i 下子选项 j
    /// 选中的子-子选项索引（缺省 0，越界由 <c>ResolveEffective</c> 钳制）。
    /// 第一层子选项未选中嵌套时该选项对应内层列表为空。
    /// </summary>
    public List<List<int>>? SubOptionSelections { get; set; }

    public bool IsEmpty => LegacySelectedIndex is null && OptionToggles is null && OptionSelections is null
        && SubOptionSelections is null;

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
/// 与旧版管理器语义对齐：v0 只部署选中互斥项对应的子目录；v1 部署所有启用选项 Include 目录
/// （有子选项时取选中的那个子选项的 Include）。清单未声明映射（Include 缺失）时回退全量部署。
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
            for (var i = 0; i < options.Count; i++)
            {
                var toggle = i < state.OptionToggles?.Count
                    ? state.OptionToggles[i]
                    : options[i].DefaultEnabled ?? true;
                toggles.Add(toggle);

                var subCount = options[i].SubOptions?.Count ?? 0;
                var selected = i < state.OptionSelections?.Count ? state.OptionSelections[i] : 0;
                selections.Add(subCount > 0 ? Math.Clamp(selected, 0, subCount - 1) : 0);

                // 嵌套子选项：每个一级子选项各自维护一个选中索引（缺省 0、越界钳制）
                var row = new List<int>();
                var subs = options[i].SubOptions ?? new List<ModSubOption>();
                for (var j = 0; j < subs.Count; j++)
                {
                    var nestedCount = subs[j].SubOptions?.Count ?? 0;
                    var storedSub = state.SubOptionSelections;
                    var storedRow = storedSub is not null &&
                        i < storedSub.Count && j < storedSub[i]?.Count
                        ? storedSub[i]![j] : 0;
                    row.Add(nestedCount > 0 ? Math.Clamp(storedRow, 0, nestedCount - 1) : 0);
                }
                subSelections.Add(row);
            }

            return new ProfileModOptionsState
            {
                OptionToggles = toggles,
                OptionSelections = selections,
                SubOptionSelections = subSelections.Any(row => row.Count > 0) ? subSelections : null
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
            var directories = new List<string>();
            for (var i = 0; i < options.Count; i++)
            {
                if (i >= toggles.Count || !toggles[i])
                {
                    continue;
                }

                AddDirectories(directories, options[i].Include, options[i].SubOptions, i, selections, subSelections);
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
    /// 递归收集节点的文件目录：节点自身 Include（声明了才加）+ 选中子分支下钻。
    /// 分支索引：一级取 selections[optionIndex]，第二层取 subSelections[optionIndex][subIndex]，
    /// 更深的层级暂无选择状态，一律取第 0 个（当前已知模组最深 3 级）。
    /// </summary>
    private static void AddDirectories(
        List<string> directories,
        List<string>? include,
        List<ModSubOption>? subOptions,
        int optionIndex,
        List<int> selections,
        List<List<int>>? subSelections)
    {
        if (include != null)
        {
            directories.AddRange(include);
        }

        if (subOptions is not { Count: > 0 })
        {
            return;
        }

        var subIndex = optionIndex < selections.Count
            ? Math.Clamp(selections[optionIndex], 0, subOptions.Count - 1)
            : 0;
        var nested = subOptions[subIndex];

        if (nested.Include != null)
        {
            directories.AddRange(nested.Include);
        }

        if (nested.SubOptions is { Count: > 0 })
        {
            var leafIndex = subSelections is { } rows && optionIndex < rows.Count
                && subIndex < rows[optionIndex]?.Count
                ? Math.Clamp(rows[optionIndex]![subIndex], 0, nested.SubOptions.Count - 1)
                : 0;
            var leaf = nested.SubOptions[leafIndex];
            if (leaf.Include != null)
            {
                directories.AddRange(leaf.Include);
            }
        }
    }
}
