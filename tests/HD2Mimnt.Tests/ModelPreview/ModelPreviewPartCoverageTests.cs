using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.ModelPreview;

public sealed class ModelPreviewPartCoverageTests
{
    [Fact]
    public void Selection_EveryPartOptionSwapsOnlyItsOwnUnits()
    {
        var groups = Enumerable.Range(0, 15).Select(index => new ModelPreviewPartGroup
        {
            Id = $"part-{index}",
            Name = $"Part {index}",
            SelectedIndex = 0,
            Options =
            [
                new() { Id = "on", Name = "On", UnitIds = [(ulong)(index * 2 + 1)] },
                new() { Id = "off", Name = "Off", UnitIds = [(ulong)(index * 2 + 2)] }
            ]
        }).ToArray();
        var meshes = groups.SelectMany(group => group.Options)
            .Select(option => Mesh(option.UnitIds[0]))
            .Append(Mesh(999))
            .ToArray();
        var defaults = groups.Select(_ => 0).ToArray();
        var baseline = ModelPreviewPartSelection.Filter(meshes, groups, defaults);
        Assert.Equal(groups.Select(group => group.Options[0].UnitIds[0]).Append(999UL), baseline.Select(mesh => mesh.UnitId));

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var selected = defaults.ToArray();
            selected[groupIndex] = 1;
            var actual = ModelPreviewPartSelection.Filter(meshes, groups, selected);
            var expected = baseline.Where(mesh => mesh.UnitId != groups[groupIndex].Options[0].UnitIds[0])
                .Append(Mesh(groups[groupIndex].Options[1].UnitIds[0]));
            Assert.Equal(expected.Select(mesh => mesh.UnitId).Order(), actual.Select(mesh => mesh.UnitId).Order());
        }
    }

    private static ModelPreviewMesh Mesh(ulong unitId) => new()
    {
        PatchFile = "p",
        UnitId = unitId,
        StreamIndex = 0,
        Positions = [0, 0, 0],
        TriangleIndices = [0, 0, 0]
    };
}
