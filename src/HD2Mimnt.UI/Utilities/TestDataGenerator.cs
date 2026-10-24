using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.UI.Utilities;

/// <summary>
/// 测试数据生成器
/// 用于生成模组测试数据
/// </summary>
public static class TestDataGenerator
{
    public static string TestModRoot => Path.GetFullPath(
        Environment.GetEnvironmentVariable("HD2MIMNT_TEST_MOD_PATH")
        ?? Path.Combine(Path.GetTempPath(), "HD2Mimnt-TestMods"));

    private static readonly Random _random = new();

    private static readonly string[] _modNamePrefixes =
    {
        "高清", "增强", "改进", "优化", "重制", "替换", "扩展", "新增",
        "HD", "Enhanced", "Improved", "Remastered", "Ultimate", "Advanced"
    };

    private static readonly string[] _modNameTypes =
    {
        "护甲", "武器", "载具", "地形", "界面", "音效", "特效", "纹理",
        "Armor", "Weapon", "Vehicle", "Terrain", "UI", "Sound", "Effects", "Texture"
    };

    private static readonly string[] _authors =
    {
        "ModAuthor", "TestUser", "Developer", "Creator", "Modder",
        "张三", "李四", "王五", "赵六", "钱七"
    };

    private static readonly string[] _descriptions =
    {
        "这是一个高质量的模组，提升游戏体验。",
        "完全重制的资源包，包含大量新内容。",
        "优化游戏性能，减少卡顿。",
        "增强视觉效果，提供更好的画面。",
        "This mod enhances the gameplay experience.",
        "Complete overhaul with new features.",
        "Performance optimization and bug fixes.",
        "Visual enhancement with HD textures."
    };

    /// <summary>
    /// 生成指定数量的测试模组
    /// </summary>
    public static List<Mod> GenerateMods(int count)
    {
        var mods = new List<Mod>();
        var now = DateTime.UtcNow;

        for (int i = 0; i < count; i++)
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = GenerateModName(i),
                Version = GenerateVersion(),
                Author = _authors[_random.Next(_authors.Length)],
                Description = _descriptions[_random.Next(_descriptions.Length)],
                Path = Path.Combine(TestModRoot, $"Mod_{i:D5}"),
                Category = (ModCategory)_random.Next(0, 8),
                Status = ModStatus.Ready,
                Tags = GenerateTags(),
                Size = _random.Next(1024 * 100, 1024 * 1024 * 500), // 100KB - 500MB
                CreatedAt = now.AddDays(-_random.Next(1, 365)),
                ImportedAt = now.AddDays(-_random.Next(0, 30)),
                ModifiedAt = now.AddDays(-_random.Next(0, 7)),
                ThumbnailPath = string.Empty,
                Hash = Guid.NewGuid().ToString("N"),
                Source = ModSource.LocalFolder,
                ContainsScripts = _random.Next(0, 10) > 7,
                WorkflowType = "LegacyWorkflow"
            };

            mods.Add(mod);
        }

        return mods;
    }

    /// <summary>
    /// 生成模组名称
    /// </summary>
    private static string GenerateModName(int index)
    {
        var prefix = _modNamePrefixes[_random.Next(_modNamePrefixes.Length)];
        var type = _modNameTypes[_random.Next(_modNameTypes.Length)];
        return $"HD2Mimnt Test {prefix}{type} v{index % 100}";
    }

    /// <summary>
    /// 生成版本号
    /// </summary>
    private static string GenerateVersion()
    {
        return $"{_random.Next(1, 10)}.{_random.Next(0, 20)}.{_random.Next(0, 100)}";
    }

    /// <summary>
    /// 生成标签列表
    /// </summary>
    private static List<string> GenerateTags()
    {
        var allTags = new[] { "高清", "优化", "增强", "新手友好", "平衡", "困难", "简单", "美化", "HD", "Enhanced", "Balanced" };
        var count = _random.Next(0, 4);
        var tags = new List<string>();

        for (int i = 0; i < count; i++)
        {
            var tag = allTags[_random.Next(allTags.Length)];
            if (!tags.Contains(tag))
                tags.Add(tag);
        }

        return tags;
    }
}
