using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

public sealed class HellForgeModMarksTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ResolveSynced_PrefersRuntimeThenConflictThenFlattened()
    {
        Assert.Equal(HellForgeModMark.NeedsRuntime, HellForgeModMarkRules.ResolveSynced(true, true, true));
        Assert.Equal(HellForgeModMark.OrderSensitive, HellForgeModMarkRules.ResolveSynced(true, false, true));
        Assert.Equal(HellForgeModMark.Flattened, HellForgeModMarkRules.ResolveSynced(true, false, false));
        Assert.Equal(HellForgeModMark.Available, HellForgeModMarkRules.ResolveSynced(false, false, false));
    }

    [Fact]
    public void FindCrossGroupConflicts_FlagsBothSidesOfASharedBundle()
    {
        var cosmetic = new Dictionary<Guid, IReadOnlyCollection<string>>
        {
            [A] = ["9ba626afa44a3aa3"]
        };
        var gameplay = new Dictionary<Guid, IReadOnlyCollection<string>>
        {
            [B] = ["9ba626afa44a3aa3", "047c26f2f11d253e"],
            [C] = ["047c26f2f11d253e"]
        };

        var conflicts = HellForgeModMarkRules.FindCrossGroupConflicts(cosmetic, gameplay);

        Assert.True(conflicts[A].SetEquals(new[] { B }));
        Assert.True(conflicts[B].SetEquals(new[] { A }));
        Assert.False(conflicts.ContainsKey(C));
    }

    [Fact]
    public void FindCrossGroupConflicts_IgnoresModsInTheSameGroup()
    {
        var cosmetic = new Dictionary<Guid, IReadOnlyCollection<string>>
        {
            [A] = ["9ba626afa44a3aa3"],
            [B] = ["9ba626afa44a3aa3"]
        };

        Assert.Empty(HellForgeModMarkRules.FindCrossGroupConflicts(
            cosmetic, new Dictionary<Guid, IReadOnlyCollection<string>>()));
    }
}
