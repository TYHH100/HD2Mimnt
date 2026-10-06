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

    public bool IsEmpty => LegacySelectedIndex is null && OptionToggles is null && OptionSelections is null;

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
            for (var i = 0; i < options.Count; i++)
            {
                var toggle = i < state.OptionToggles?.Count
                    ? state.OptionToggles[i]
                    : options[i].DefaultEnabled ?? true;
                toggles.Add(toggle);

                var subCount = options[i].SubOptions?.Count ?? 0;
                var selected = i < state.OptionSelections?.Count ? state.OptionSelections[i] : 0;
                selections.Add(subCount > 0 ? Math.Clamp(selected, 0, subCount - 1) : 0);
            }

            return new ProfileModOptionsState
            {
                OptionToggles = toggles,
                OptionSelections = selections
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
    /// 部署时的文件收集范围。只有清单声明了完整映射（每个 v1 选项都有 Include、
    /// 每个子选项都有 Include）才能按选项过滤；映射不完整时回退全量部署保持兼容。
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
            // Include == null 表示清单未声明映射；空列表是显式的“无文件占位”，两者语义不同
            if (options.Any(o => o.Include == null)
                || options.Any(o => o.SubOptions?.Any(s => s.Include == null) == true))
            {
                return new OptionDeployPlan { Scope = OptionDeployScope.FullDirectory };
            }

            var toggles = state.OptionToggles ?? new List<bool>(options.Select(o => o.DefaultEnabled ?? true));
            var selections = state.OptionSelections ?? new List<int>(new int[options.Count]);
            var directories = new List<string>();
            for (var i = 0; i < options.Count; i++)
            {
                if (i >= toggles.Count || !toggles[i])
                {
                    continue;
                }

                directories.AddRange(options[i].Include!);

                var subOptions = options[i].SubOptions;
                if (subOptions is { Count: > 0 })
                {
                    var subIndex = i < selections.Count ? Math.Clamp(selections[i], 0, subOptions.Count - 1) : 0;
                    directories.AddRange(subOptions[subIndex].Include!);
                }
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
}
