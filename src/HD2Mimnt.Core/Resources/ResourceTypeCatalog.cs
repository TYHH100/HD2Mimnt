namespace HD2Mimnt.Core.Resources;

public sealed record ResourceTypeDefinition(ulong Id, string Name, string DisplayName)
{
    public string HexId => $"0x{Id:X16}";
    public string Label => $"{DisplayName} ({Name})";
}

/// <summary>对应用户提供的 HD2SDK Community Edition constants.py；ID 是唯一匹配依据。</summary>
public static class ResourceTypeCatalog
{
    public static IReadOnlyList<ResourceTypeDefinition> All { get; } = Array.AsReadOnly(new ResourceTypeDefinition[]
    {
        new(0x18dead01056b72e9UL, "bones", "骨骼"),
        new(0x2a690fd348fe9ac5UL, "level", "关卡"),
        new(0x5f7203c8f280dab8UL, "physics", "物理"),
        new(0x931e336d7646cc26UL, "animation", "动画"),
        new(0xa486d4045106165cUL, "state_machine", "状态机"),
        new(0xa8193123526fad64UL, "particles", "粒子"),
        new(0xab2f78e885f513c6UL, "prefab", "预制体"),
        new(0xcd4238c6a0c69e32UL, "texture", "纹理"),
        new(0xe0a48d0be9a7453fUL, "mesh", "模型"),
        new(0xeac0b497876adedfUL, "material", "材质"),
        new(0x535a7bd3e650d799UL, "wwise_bank", "音频库"),
        new(0xaf32095c82f2b070UL, "wwise_dep", "音频依赖"),
        new(0x504b55235d21440eUL, "wwise_stream", "音频流"),
        new(0x250e0a11ac8e26f8UL, "shading_environment_mapping", "着色环境映射"),
        new(0x9831ca893b0d087dUL, "entity", "实体"),
        new(0xaa5965f03029fa18UL, "bik", "视频"),
        new(0x9efe0a916aae7880UL, "font", "字体"),
        new(0xd7014a50477953e0UL, "cloth", "布料"),
        new(0xfe73c7dcff8a7ca5UL, "shading_environment", "着色环境"),
        new(0xa14e8dfa2cd117e2UL, "lua", "Lua 脚本"),
        new(0xf7505933166d6755UL, "vector_field", "向量场"),
        new(0x92d3ee038eeb610dUL, "flow", "流程"),
        new(0x27862fe24795319cUL, "render_config", "渲染配置"),
        new(0x0d972bab10b40fd3UL, "strings", "字符串"),
        new(0x3b1fa9e8f6bac374UL, "network_config", "网络配置"),
        new(0x82645835e6b73232UL, "config", "配置"),
        new(0xad9c6d9ed1e5e77aUL, "package", "资源包"),
        new(0xd50a8b7e1c82b110UL, "wwise_metadata", "音频元数据"),
        new(0xb277b11fe4a61d37UL, "mouse_cursor", "鼠标指针"),
        new(0x7910103158fc1de9UL, "renderable", "可渲染对象"),
        new(0x9e5c3cc74575aeb5UL, "shader_library_group", "着色器库组"),
        new(0xe5ee32a477239a93UL, "shader_library", "着色器库"),
        new(0x05106b81dcd58a13UL, "runtime_font", "运行时字体"),
        new(0x1d59bd6687db6b33UL, "ragdoll_profile", "布娃娃配置"),
        new(0x2a0a70acfe476e1dUL, "ah_bin", "AH 二进制"),
        new(0x57a13425279979d7UL, "ik_skeleton", "IK 骨架"),
        new(0x5fdd5fe391076f9fUL, "wwise_properties", "音频属性"),
        new(0x6592b918e67f082cUL, "havok_ai_properties", "Havok AI 属性"),
        new(0x9199bb50b6896f02UL, "texture_atlas", "纹理图集"),
        new(0xb8fd4d2cede20ed7UL, "geleta", "Geleta"),
        new(0xc4f0f4be7fb0c8d6UL, "geometry_group", "几何组"),
        new(0xe3f2851035957af5UL, "hash_lookup", "哈希查询"),
        new(0xe985c5f61c169997UL, "speedtree", "SpeedTree"),
        new(0xf7a09f8bb35a1d49UL, "havok_physics_properties", "Havok 物理属性"),
        new(0xfcaaf813b4d3cc1eUL, "camera_shake", "镜头震动"),
        new(0x5ee65304478f8db5UL, "bik2", "视频 BIK2"),
        new(0x46bc82aae9ae0565UL, "unknown", "未知类型（XAML）"),
    });

    private static readonly IReadOnlyDictionary<ulong, ResourceTypeDefinition> ById = All.ToDictionary(type => type.Id);
    public static string GetDisplayName(ulong id) => ById.TryGetValue(id, out var type)
        ? type.DisplayName : $"未知资源 0x{id:X16}";
    public static bool IsKnown(ulong id) => ById.ContainsKey(id);
}