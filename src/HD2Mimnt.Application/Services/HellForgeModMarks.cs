namespace HD2Mimnt.Application.Services;

/// <summary>HellForge 模式下模组卡片上的派生标记（只在 HellForge 模式显示，不落库、不写 Tags）。</summary>
public enum HellForgeModMark
{
    /// <summary>干净外观包，导出分类与加载顺序都与官方部署一致。</summary>
    Available,

    /// <summary>非干净 V1 包，导出为扁平模组；在 HellForge 里会被排到外观包之后加载。</summary>
    Flattened,

    /// <summary>补丁含 Lua 脚本，HellForge 需要玩家自装 Bingus Shared Loader 或 HD2Runtime，否则不生效。</summary>
    NeedsRuntime,

    /// <summary>与其他启用模组修改同一资源包，HellForge 的分组顺序会翻转它们的覆盖关系。</summary>
    OrderSensitive,

    /// <summary>不参与 HellForge 同步（加载器/依赖、文件缺失、未同步）。</summary>
    NotSynced,

    /// <summary>HellForge 不支持（HD2IAS 等）。</summary>
    Unsupported
}

/// <summary>一个模组的 HellForge 标记：<paramref name="Detail"/> 是附加说明（冲突模组名等），为空表示无。</summary>
public sealed record HellForgeModMarkInfo(Guid ModId, HellForgeModMark Mark, string Detail = "");

/// <summary>
/// 标记判定规则（纯函数，便于单测）：已同步模组按“需运行时 &gt; 顺序敏感 &gt; 已扁平 &gt; 可用”取最高优先级；
/// 顺序敏感只看**跨组**冲突（外观包 × 扁平模组）——同组内相对顺序不变，只有 HellForge 把外观包整组前置时才会翻转覆盖。
/// </summary>
public static class HellForgeModMarkRules
{
    public static HellForgeModMark ResolveSynced(bool flattened, bool needsRuntime, bool orderSensitive)
    {
        if (needsRuntime) return HellForgeModMark.NeedsRuntime;
        if (orderSensitive) return HellForgeModMark.OrderSensitive;
        return flattened ? HellForgeModMark.Flattened : HellForgeModMark.Available;
    }

    /// <summary>
    /// 跨组冲突：返回 模组 → 另一组里修改同一 16 位 bundle key 的模组；只包含真正有冲突的模组。
    /// </summary>
    public static Dictionary<Guid, HashSet<Guid>> FindCrossGroupConflicts(
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> cosmeticBundles,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> gameplayBundles)
    {
        var cosmeticByBundle = GroupByBundle(cosmeticBundles);
        var gameplayByBundle = GroupByBundle(gameplayBundles);
        var conflicts = new Dictionary<Guid, HashSet<Guid>>();
        void Link(IEnumerable<Guid> mods, IEnumerable<Guid> others)
        {
            foreach (var modId in mods)
            {
                if (!conflicts.TryGetValue(modId, out var set))
                    conflicts[modId] = set = new HashSet<Guid>();
                set.UnionWith(others);
            }
        }
        foreach (var (bundle, cosmetic) in cosmeticByBundle)
            if (gameplayByBundle.TryGetValue(bundle, out var gameplay))
            {
                Link(cosmetic, gameplay);
                Link(gameplay, cosmetic);
            }
        return conflicts;
    }

    static Dictionary<string, List<Guid>> GroupByBundle(IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> bundlesByMod)
    {
        var owners = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var (modId, bundles) in bundlesByMod)
            foreach (var bundle in bundles)
            {
                if (!owners.TryGetValue(bundle, out var list)) owners[bundle] = list = new List<Guid>();
                if (!list.Contains(modId)) list.Add(modId);
            }
        return owners;
    }

    internal sealed class Entry
    {
        public string Mark { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }
}
