using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Tests.Services;

/// <summary>addon 扫描：选定 addon 后应直接给出可转换模型清单，并说明被跳过的模型。</summary>
public sealed class GmodAddonAnalysisTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"HD2Mimnt-GmodAnalysis-{Guid.NewGuid():N}")).FullName;
    private readonly GmodIasConversionService _service = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Analyze_ListsPlayerModelFirstAndFlagsIncompleteOnes()
    {
        var addon = Directory.CreateDirectory(Path.Combine(_root, "addon")).FullName;
        WriteModel(Path.Combine(addon, "models", "props", "broken.mdl"), "broken");
        WriteModel(Path.Combine(addon, "models", "player", "main.mdl"), "main");
        WriteModelSiblings(Path.Combine(addon, "models", "player", "main"), physics: true);
        File.WriteAllText(Path.Combine(addon, "readme.txt"), "not a model");

        var analysis = await _service.AnalyzeAsync(addon);

        Assert.Equal(2, analysis.Models.Count);
        Assert.Empty(analysis.Issues);

        var player = analysis.Models[0];
        Assert.Equal("models/player/main.mdl", player.Path);
        Assert.Equal("main", player.Name);
        Assert.True(player.IsConvertible);
        Assert.True(player.HasVertexData && player.HasIndexData && player.HasPhysics);
        Assert.Equal(1, player.BoneCount);
        Assert.Equal(1, player.MeshCount);
        Assert.Equal(2, player.MaterialCount);
        Assert.Equal(1, player.BodyGroupCount);
        Assert.Equal(2, player.BodyGroupOptionCount);
        Assert.Equal(2, player.SkinCount);

        var broken = analysis.Models[1];
        Assert.Equal("models/props/broken.mdl", broken.Path);
        Assert.False(broken.IsConvertible);
        Assert.False(broken.HasVertexData);
    }

    [Fact]
    public async Task Analyze_UnreadableModel_ReportsIssueAndKeepsGoing()
    {
        var addon = Directory.CreateDirectory(Path.Combine(_root, "addon")).FullName;
        WriteModel(Path.Combine(addon, "models", "player", "main.mdl"), "main");
        WriteModelSiblings(Path.Combine(addon, "models", "player", "main"), physics: false);
        var corrupt = Path.Combine(addon, "models", "player", "corrupt.mdl");
        File.WriteAllBytes(corrupt, Encoding.ASCII.GetBytes("not an mdl file"));

        var analysis = await _service.AnalyzeAsync(addon);

        var model = Assert.Single(analysis.Models, item => item.IsConvertible);
        Assert.Equal("models/player/main.mdl", model.Path);
        var issue = Assert.Single(analysis.Issues);
        Assert.Equal("UnreadableModel", issue.Code);
        Assert.Contains("corrupt.mdl", issue.Asset);
    }

    [Fact]
    public async Task Analyze_MissingAddon_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _service.AnalyzeAsync(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task Analyze_EmptyFolder_ReturnsNoModels()
    {
        var addon = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        var analysis = await _service.AnalyzeAsync(addon);
        Assert.Empty(analysis.Models);
        Assert.Empty(analysis.Issues);
    }

    private static void WriteModel(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Mdl(name));
    }

    private static void WriteModelSiblings(string stem, bool physics)
    {
        File.WriteAllBytes(stem + ".vvd", Vvd());
        File.WriteAllBytes(stem + ".dx90.vtx", Vtx());
        if (physics) File.WriteAllBytes(stem + ".phy", [0x01, 0x02]);
    }

    private static void Int(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    private static void Short(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Float(byte[] data, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
    private static void Text(byte[] data, int offset, string value) => Encoding.UTF8.GetBytes(value).CopyTo(data, offset);

    private static byte[] Mdl(string name)
    {
        var data = new byte[1500];
        Text(data, 0, "IDST"); Int(data, 4, 48); Int(data, 8, 123); Text(data, 12, name); Int(data, 76, data.Length);
        Int(data, 156, 1); Int(data, 160, 408);
        Int(data, 408, 216); Int(data, 412, -1); Text(data, 624, "ValveBiped.Bip01_Pelvis");
        Float(data, 408 + 56, 1); Float(data, 408 + 156, 1);
        Float(data, 408 + 96, 1); Float(data, 408 + 116, 1); Float(data, 408 + 136, 1);
        Int(data, 204, 2); Int(data, 208, 656);
        Int(data, 656, 128); Text(data, 784, "base");
        Int(data, 720, 72); Text(data, 792, "variant");
        Int(data, 212, 1); Int(data, 216, 808); Int(data, 808, 812); Text(data, 812, "fixture/");
        Int(data, 220, 1); Int(data, 224, 2); Int(data, 228, 824);
        Short(data, 824, 0); Short(data, 826, 1);
        Int(data, 232, 1); Int(data, 236, 832);
        Int(data, 832, 16); Int(data, 836, 2); Int(data, 840, 1); Int(data, 844, 32); Text(data, 848, "hat");
        Text(data, 864, "visible"); Int(data, 864 + 72, 1); Int(data, 864 + 76, 296); Int(data, 864 + 80, 3);
        Text(data, 1012, "blank");
        Int(data, 1160 + 8, 3); Int(data, 1160 + 52, 3);
        return data;
    }

    private static byte[] Vvd()
    {
        var data = new byte[256];
        Text(data, 0, "IDSV"); Int(data, 4, 4); Int(data, 8, 123); Int(data, 12, 1); Int(data, 16, 3);
        Int(data, 56, 64); Int(data, 60, 208);
        for (var i = 0; i < 3; i++)
        {
            var offset = 64 + i * 48;
            Float(data, offset, 1); data[offset + 15] = 1;
            Float(data, offset + 16, i == 1 ? 1 : 0); Float(data, offset + 20, i == 2 ? 1 : 0);
            Float(data, offset + 36, 1);
            Float(data, 208 + i * 16, 1); Float(data, 220 + i * 16, 1);
        }
        return data;
    }

    private static byte[] Vtx()
    {
        var data = new byte[178];
        Int(data, 0, 7); Int(data, 16, 123); Int(data, 20, 1); Int(data, 28, 1); Int(data, 32, 36);
        Int(data, 36, 2); Int(data, 40, 8);
        Int(data, 44, 1); Int(data, 48, 16);
        Int(data, 52, 1); Int(data, 56, 20);
        Int(data, 60, 1); Int(data, 64, 24);
        Int(data, 84, 1); Int(data, 88, 9);
        Int(data, 93, 3); Int(data, 97, 25);
        Int(data, 101, 3); Int(data, 105, 52);
        Int(data, 109, 1); Int(data, 113, 58);
        for (var i = 0; i < 3; i++)
        {
            data[118 + i * 9 + 3] = 1;
            Short(data, 118 + i * 9 + 4, (ushort)i);
            Short(data, 145 + i * 2, (ushort)i);
        }
        Int(data, 151, 3); Int(data, 159, 3); Short(data, 167, 1); data[169] = 1;
        return data;
    }
}
