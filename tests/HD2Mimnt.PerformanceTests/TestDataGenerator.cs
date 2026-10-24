using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// Generates test data for performance testing
/// </summary>
public class TestDataGenerator
{
    private static readonly string[] ModNamePrefixes = new[]
    {
        "Enhanced", "Improved", "Ultimate", "Advanced", "Better", "Realistic", "HD", "4K",
        "Optimized", "Overhaul", "Complete", "Total", "Mega", "Super", "Classic", "Modern"
    };

    private static readonly string[] ModNameTypes = new[]
    {
        "Weapons", "Armor", "Graphics", "UI", "Sound", "Gameplay", "Balance", "Difficulty",
        "Economy", "AI", "Performance", "Textures", "Models", "Effects", "HUD", "Menu"
    };

    private static readonly string[] Authors = new[]
    {
        "ModderPro", "GameEnhancer", "QualityMods", "BestMods", "EliteModder",
        "ProGamer", "ModMaster", "DevTeam", "CommunityMod", "IndieModder",
        "TechWizard", "ArtistMod", "CodeNinja", "DesignGuru", "BalanceExpert"
    };

    private static readonly ModCategory[] Categories = Enum.GetValues<ModCategory>();

    /// <summary>
    /// Generate test mods with realistic variety
    /// </summary>
    public static List<Mod> GenerateMods(int count)
    {
        var mods = new List<Mod>(count);
        var random = new Random(42); // Fixed seed for reproducibility

        for (int i = 0; i < count; i++)
        {
            var prefix = ModNamePrefixes[i % ModNamePrefixes.Length];
            var type = ModNameTypes[(i / ModNamePrefixes.Length) % ModNameTypes.Length];
            var suffix = i > 100 ? $" {i}" : "";

            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = $"{prefix} {type}{suffix}",
                Version = GenerateVersion(random),
                Author = Authors[random.Next(Authors.Length)],
                Category = Categories[random.Next(Categories.Length)],
                Description = $"This is a test mod for performance testing. Mod index: {i}. " +
                             $"It enhances {type.ToLower()} in the game with {prefix.ToLower()} features.",
                Path = $"TestMods/Mod_{i:D5}",
                ImportedAt = DateTime.UtcNow.AddDays(-random.Next(0, 365)),
                ModifiedAt = DateTime.UtcNow.AddDays(-random.Next(0, 30)),
                CreatedAt = DateTime.UtcNow.AddDays(-random.Next(365, 730)),
                Size = random.Next(1024 * 100, 1024 * 1024 * 50), // 100KB to 50MB
                ThumbnailPath = random.Next(100) < 70 ? $"TestThumbnails/thumb_{i:D5}.png" : string.Empty, // 70% have thumbnails
                Tags = GenerateTags(random, i)
            };

            mods.Add(mod);
        }

        return mods;
    }

    private static string GenerateVersion(Random random)
    {
        var major = random.Next(1, 5);
        var minor = random.Next(0, 20);
        var patch = random.Next(0, 10);
        return $"{major}.{minor}.{patch}";
    }

    private static List<string> GenerateTags(Random random, int index)
    {
        var allTags = new[] { "popular", "tested", "new", "updated", "essential", "optional", "beta", "stable" };
        var tagCount = random.Next(0, 4);
        return Enumerable.Range(0, tagCount)
            .Select(_ => allTags[random.Next(allTags.Length)])
            .Distinct()
            .ToList();
    }
}
