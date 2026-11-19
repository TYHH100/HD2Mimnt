using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Resources;

namespace HD2Mimnt.Application.Search;

/// <summary>
/// 系统徽标对应的虚拟标签：不写入标签列，仅参与 @ 搜索，
/// 让界面上的所有系统分类（HD2IAS、Lua、资源类型、来源平台）都能直接搜到。
/// </summary>
public static class ModSystemTags
{
    public const string Hd2Ias = "HD2IAS";
    public const string LuaMods = "Lua Mods";
    public const string AyakaMods = "Ayaka Mods";
    public const string NexusMods = "Nexus Mods";
    private const ulong LuaResourceTypeId = 0xa14e8dfa2cd117e2UL;

    public static IEnumerable<string> For(Mod mod)
    {
        if (string.Equals(mod.WorkflowType, Hd2Ias, StringComparison.OrdinalIgnoreCase))
            yield return Hd2Ias;
        if (mod.ResourceTypeIds?.Contains(LuaResourceTypeId) == true
            || (mod.BslRole.HasValue && mod.BslRole.Value != BslModRole.None))
            yield return LuaMods;
        foreach (var id in mod.ResourceTypeIds ?? [])
            if (ResourceTypeCatalog.IsKnown(id))
                yield return ResourceTypeCatalog.GetDisplayName(id);
        switch (mod.PlatformLink?.PlatformId)
        {
            case "ayakamods": yield return AyakaMods; break;
            case "nexusmods": yield return NexusMods; break;
        }
    }
}
