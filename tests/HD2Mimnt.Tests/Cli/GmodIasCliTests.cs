using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Application.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.Cli;

/// <summary>GMOD → HD2 IAS 转换的 CLI 注册、参数校验与真实落盘验证。</summary>
public sealed class GmodIasCliTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"HD2Mimnt-GmodIasCli-{Guid.NewGuid():N}")).FullName;
    private readonly IServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Help_ListsGmodIasConvertCommand()
    {
        var help = CliCommandCatalog.Build().BuildHelpText();
        Assert.Contains("gmod-ias convert", help);
        Assert.Contains("GMOD",
            Assert.Single(CliCommandCatalog.Build().Commands, command => command.Name == "gmod-ias convert").Summary);
    }

    [Fact]
    public async Task Convert_WithoutYes_RequiresExplicitConfirmation()
    {
        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "convert", "a.gma", "b.mdl", "out"], _services, json: false, yes: false, CancellationToken.None);
        Assert.True(response.RequiresYes);
        Assert.Equal(2, response.ExitCode);
    }

    [Fact]
    public async Task Convert_TooFewArguments_ReturnsUsage()
    {
        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "convert", "only-one"], _services, json: false, yes: true, CancellationToken.None);
        Assert.Equal(2, response.ExitCode);
    }

    [Fact]
    public async Task Convert_UnknownFlag_ReturnsUsage()
    {
        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "convert", "--bogus", "a", "b", "c"], _services, json: false, yes: true, CancellationToken.None);
        Assert.Equal(2, response.ExitCode);
        Assert.Contains("未知选项", response.Message);
    }

    [Fact]
    public async Task Convert_MissingAddon_ReturnsFailure()
    {
        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "convert", Path.Combine(_root, "missing.gma"), "x.mdl", Path.Combine(_root, "out")],
            _services, json: false, yes: true, CancellationToken.None);
        Assert.Equal(1, response.ExitCode);
    }

    [Fact]
    public async Task Convert_FixtureAddon_WritesCompleteBundleToDisk()
    {
        var addon = CreateFixtureAddon();
        var output = Path.Combine(_root, "output");

        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "convert", addon, "fixture.mdl", output, "--project", "cli-fixture",
                "--archive", "0xabcdef", "--native", "0x101", "--split", "none"],
            _services, json: false, yes: true, CancellationToken.None);

        Assert.True(response.ExitCode == 0, response.Message);
        Assert.Contains("转换完成", response.Message);
        foreach (var name in new[] { "manifest.json", "conversion-report.json", "geometry/model.hd2geom",
            "materials/materials.json", "source/index.json" })
            Assert.True(File.Exists(Path.Combine(output, name)), $"缺少产物 {name}");

        // 运行时配套按 HD2 独立封包的命名规则落盘：项目身份 32 位十六进制、
        // 隔离/差分清单的文件名等于文件内部声明的 ID。
        var runtime = Directory.GetFiles(Path.Combine(output, "ias")).Select(Path.GetFileName).ToArray();
        Assert.Contains(runtime, name => System.Text.RegularExpressions.Regex.IsMatch(name!,
            "^[0-9a-f]{32}\\.0*abcdef\\.rigbin$"));
        Assert.Contains(runtime, name => System.Text.RegularExpressions.Regex.IsMatch(name!,
            "^owned_[0-9a-f]{24}\\.isolation\\.ini$"));
        Assert.Contains(runtime, name => System.Text.RegularExpressions.Regex.IsMatch(name!,
            "^hd2free_[0-9a-f]{16}\\.difference\\.ini$"));
        Assert.True(File.Exists(Path.Combine(output, "cli-fixture.hd2irpack.zip")), "缺少配套 .hd2irpack.zip");
    }

    [Fact]
    public async Task Convert_DefaultSplit_WritesBodyBundleOnlyWhenNoHeadGeometry()
    {
        // fixture 没有头骨：默认 both 拆分只产出 body/ 包，且根 manifest 仍在。
        var addon = CreateFixtureAddon();
        var output = Path.Combine(_root, "output-split");

        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "convert", addon, "fixture.mdl", output, "--project", "cli-fixture",
                "--archive", "0xabcdef", "--native", "0x101"],
            _services, json: false, yes: true, CancellationToken.None);

        Assert.True(response.ExitCode == 0, response.Message);
        Assert.Contains("body/", response.Message);
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "body", "part-manifest.json")));
        // 部位目录里的 manifest.json 属于可直接导入的 data 模组包，部位自身的索引另存 part-manifest.json。
        Assert.True(File.Exists(Path.Combine(output, "body", "package", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "body", "geometry", "model.hd2geom")));
        Assert.True(Directory.Exists(Path.Combine(output, "body", "ias")), "部位目录缺少 ias/ 运行时文件");
        Assert.False(Directory.Exists(Path.Combine(output, "helmet")));
    }

    [Fact]
    public async Task Convert_BadSwitchSelection_ReturnsUsage()
    {
        var addon = CreateFixtureAddon();
        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "convert", addon, "fixture.mdl", Path.Combine(_root, "out2"),
                "--switches", "skin,bogus"],
            _services, json: false, yes: true, CancellationToken.None);
        Assert.Equal(2, response.ExitCode);
        Assert.Contains("--switches", response.Message);
    }

    [Fact]
    public async Task List_FixtureAddon_ReportsConvertibleModelsAndGroups()
    {
        var addon = CreateFixtureAddon();
        var response = await CliCommandCatalog.Build().DispatchAsync(
            ["gmod-ias", "list", addon, "--groups"],
            _services, json: false, yes: true, CancellationToken.None);
        Assert.True(response.ExitCode == 0, response.Message);
        Assert.Contains("fixture.mdl", response.Message);
        Assert.Contains("hat", response.Message);
    }

    private string CreateFixtureAddon()
    {
        var addon = Directory.CreateDirectory(Path.Combine(_root, "addon")).FullName;
        Directory.CreateDirectory(Path.Combine(addon, "materials", "fixture"));
        File.WriteAllBytes(Path.Combine(addon, "fixture.mdl"), Mdl());
        File.WriteAllBytes(Path.Combine(addon, "fixture.vvd"), Vvd());
        File.WriteAllBytes(Path.Combine(addon, "fixture.dx90.vtx"), Vtx());
        var material = "\"VertexLitGeneric\"\n{\n\"$basetexture\" \"textures/base\"\n}\n";
        File.WriteAllText(Path.Combine(addon, "materials", "fixture", "base.vmt"), material);
        File.WriteAllText(Path.Combine(addon, "materials", "fixture", "variant.vmt"), material);
        return addon;
    }

    private static void Int(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    private static void Short(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Float(byte[] data, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
    private static void Text(byte[] data, int offset, string value) => Encoding.UTF8.GetBytes(value).CopyTo(data, offset);

    private static byte[] Mdl()
    {
        var data = new byte[1500];
        Text(data, 0, "IDST"); Int(data, 4, 48); Int(data, 8, 123); Text(data, 12, "fixture"); Int(data, 76, data.Length);
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
