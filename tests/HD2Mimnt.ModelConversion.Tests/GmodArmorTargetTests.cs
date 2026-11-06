using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2Mimnt.ModelConversion.Gmod;
using HD2Mimnt.ModelConversion.Gmod.Packaging;
using HD2Mimnt.ModelConversion.Gmod.Parts;
using HD2Mimnt.ModelConversion.Gmod.Targets;
using HD2Mimnt.ModelConversion.Hd2;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Tests;

/// <summary>
/// 目标护甲目录与「接管一个载体 Unit + 清空其余甲片」的路由策略。
/// 没有这个绑定，产物在游戏里不会挂到任何护甲上——这是最容易静默失败的一环，因此单独覆盖。
/// </summary>
public sealed class GmodArmorTargetTests
{
    private const ulong Archive = 0x08241BA732C97A9D;
    private const uint Equipment = 0x7346EDC6;
    private const ulong Carrier = 0x61B3454C7E18AA01;
    private const ulong PlateA = 0x09014F38EE60FF57;
    private const ulong PlateB = 0x0A376E540AF99F43;

    [Fact]
    public void Parse_ReadsArchiveDomainCarrierAndClosure()
    {
        var targets = GmodArmorTargetCatalog.Parse(Encoding.UTF8.GetBytes("""
            {"schema":"HD2MimntArmorTargets1","targets":[
              {"archive":"08241ba732c97a9d","domain":"BODY","code":"TR-62","name":"TR-62 Knight",
               "carrier":"61b3454c7e18aa01","equipment":["7346edc6"],
               "units":["61b3454c7e18aa01","09014f38ee60ff57"]}]}
            """));

        var target = Assert.Single(targets);
        Assert.Equal(Archive, target.ArchiveId);
        Assert.Equal(IasContentDomain.Body, target.Domain);
        Assert.Equal("TR-62 Knight", target.DisplayName); // 名称已含代号时不再重复前缀
        Assert.Equal(Carrier, target.CarrierUnitId);
        Assert.Equal<uint>([0x7346EDC6], target.EquipmentKeys);
        Assert.Equal([PlateA], target.CleanupUnitIds);
        Assert.Equal("body", target.DomainName);
    }

    [Fact]
    public void Parse_RejectsCarrierOutsideItsOwnClosure()
    {
        var json = """
            {"schema":"HD2MimntArmorTargets1","targets":[
              {"archive":"08241ba732c97a9d","domain":"BODY","code":"","name":"",
               "carrier":"deadbeefdeadbeef","equipment":["7346edc6"],"units":["61b3454c7e18aa01"]}]}
            """;
        var error = Assert.Throws<Hd2FormatException>(() => GmodArmorTargetCatalog.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Contains("outside its own closure", error.Message);
    }

    [Fact]
    public void Parse_RejectsUnknownSchema()
    {
        Assert.Throws<Hd2FormatException>(() => GmodArmorTargetCatalog.Parse(
            Encoding.UTF8.GetBytes("""{"schema":"Something1","targets":[]}""")));
    }

    [Fact]
    public void Find_MatchesCodeNameAndArchive()
    {
        var targets = new[]
        {
            new GmodArmorTarget(Archive, IasContentDomain.Body, "TR-62", "TR-62 Knight", Carrier,
                [Equipment], [Carrier, PlateA])
        };
        Assert.Same(targets[0], GmodArmorTargetCatalog.Find(targets, "TR-62"));
        Assert.Same(targets[0], GmodArmorTargetCatalog.Find(targets, "tr-62"));
        Assert.Same(targets[0], GmodArmorTargetCatalog.Find(targets, "TR-62 Knight"));
        Assert.Same(targets[0], GmodArmorTargetCatalog.Find(targets, "0x08241BA732C97A9D"));
        Assert.Same(targets[0], GmodArmorTargetCatalog.Find(targets, "08241ba732c97a9d"));
        Assert.Null(GmodArmorTargetCatalog.Find(targets, "NOPE-1"));
        Assert.Null(GmodArmorTargetCatalog.Find(targets, ""));
    }

    [Fact]
    public void ShippedCatalog_HasBothDomainsAndUsableCarriers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Config", "ArmorTargets.json");
        Assert.True(File.Exists(path), $"目标护甲目录没有随程序分发：{path}");
        var targets = GmodArmorTargetCatalog.Parse(File.ReadAllBytes(path));

        Assert.True(targets.Count >= 200, $"目录只有 {targets.Count} 条记录。");
        var body = targets.Where(target => target.Domain == IasContentDomain.Body).ToArray();
        var helmet = targets.Where(target => target.Domain == IasContentDomain.Helmet).ToArray();
        Assert.NotEmpty(body);
        Assert.NotEmpty(helmet);
        Assert.All(targets, target =>
        {
            Assert.NotEqual(0UL, target.ArchiveId);
            Assert.Contains(target.CarrierUnitId, target.UnitIds);
            Assert.NotEmpty(target.EquipmentKeys);
            Assert.Equal(target.UnitIds.Count - 1, target.CleanupUnitIds.Count);
        });
        // 身体甲通常由十几个到二十几个 Unit 组成，头盔多数只有主头部件。
        Assert.Contains(body, target => target.CleanupUnitIds.Count > 10);
        Assert.Contains(helmet, target => target.CleanupUnitIds.Count == 0);
        Assert.Contains(targets, target => target.DisplayName.Contains("TR-62", StringComparison.Ordinal));
    }

    [SampleFact]
    public void TargetArmor_RoutesOnlyTheCarrierAndClearsTheRest()
    {
        var sample = RealSourceSample.FindSample()!;
        var target = new GmodArmorTarget(Archive, IasContentDomain.Body, "TR-62", "TR-62 Knight", Carrier,
            [Equipment], [PlateA, Carrier, PlateB]);
        var binding = new IasArchiveBinding(Archive, IasContentDomain.Body,
            [new IasEquipmentBinding(Equipment, target.UnitIds)]);

        using var provider = new GmaAssetProvider(sample);
        var package = SourceModelPackage.Load(provider, "models/shoubi/aes_yz_sb.mdl");
        var result = new GmodIasConverter().Convert(package, new GmodConversionOptions("armor-sample", binding,
            PartSplit: GmodPartSplit.None, IncludeSkinSwitch: false, IncludeRawSource: false, Targets: [target]),
            provider);

        // 1. 每个选项只接管载体 Unit：整身模型被复制到每个甲片槽位会成为 N 份重叠网格。
        var difference = IasFreePartsCodec.Read(
            Assert.Single(result.Files.Where(file => file.Path.EndsWith(".difference.ini", StringComparison.Ordinal))).Data.Span,
            binding);
        var options = difference.Parts.SelectMany(part => part.Options).ToArray();
        Assert.NotEmpty(options);
        var routed = options.Select(option => Assert.Single(option.Routes).NativeUnitId).ToArray();
        Assert.True(routed.All(id => id == Carrier),
            "选项路由到的 Unit: " + string.Join(", ", routed.Select(id => id.ToString("x16"))));

        // 2. isolation 清单里另外两个甲片槽位指向占位 Unit。
        var isolation = Encoding.UTF8.GetString(
            Assert.Single(result.Files.Where(file => file.Path.EndsWith(".isolation.ini", StringComparison.Ordinal))).Data.Span);
        var unitRows = Regex.Matches(isolation, @"^unit=0x([0-9A-F]{16}),0x([0-9A-F]{16})$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.Equal(3, unitRows.Count);
        Assert.Contains(unitRows, row => ulong.Parse(row.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) == Carrier);
        var profiles = Regex.Matches(isolation, @"^rig_profile=0x[0-9A-F]{16}$", RegexOptions.Multiline);
        Assert.Equal(options.Length, profiles.Count);

        // 3. 补丁里确实写了两个占位 Unit，并且共用同一份（很小的）载荷。
        var patch = Assert.Single(result.Files.Where(file => file.Kind == "patch")).Data;
        var cleanup = result.Report.Parts[0].CleanupUnitCount;
        Assert.Equal(2, cleanup);
        Assert.Equal(options.Length + cleanup, result.Report.Parts[0].UnitCount);
        var index = JsonDocument.Parse(
            Assert.Single(result.Files.Where(file => file.Path.EndsWith("hd2mimnt-gmod.json", StringComparison.Ordinal))).Data);
        var cleanupUnits = index.RootElement.GetProperty("units").EnumerateArray()
            .Where(unit => unit.GetProperty("cleanup").GetBoolean()).ToArray();
        Assert.Equal(2, cleanupUnits.Length);
        Assert.All(cleanupUnits, unit =>
        {
            // 零面积占位：一个退化三角形，既保留一个 mesh 槽位又不会真的画出来。
            Assert.True(unit.GetProperty("placeholder").GetBoolean());
            Assert.Equal(1, unit.GetProperty("mesh_count").GetInt32());
            Assert.Equal(3, unit.GetProperty("vertex_count").GetInt32());
            Assert.Equal(1, unit.GetProperty("triangle_count").GetInt32());
        });

        // 两个占位 Unit 的 file_id 不同（各自是一个槽位），但载荷落在同一段主数据与 GPU 块上。
        var cleanupIds = cleanupUnits.Select(unit => unit.GetProperty("unit_id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var cleanupEntries = index.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => cleanupIds.Contains(entry.GetProperty("file_id").GetString()!)).ToArray();
        Assert.Equal(2, cleanupEntries.Length);
        Assert.Single(cleanupEntries.Select(entry =>
            (entry.GetProperty("main_offset").GetInt32(), entry.GetProperty("main_size").GetInt32(),
             entry.GetProperty("gpu_offset").GetInt32(), entry.GetProperty("gpu_size").GetInt32())).Distinct());
        var unitTypeId = "0x" + StingrayTypeIds.Unit.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(cleanupEntries, entry => Assert.Equal(unitTypeId, entry.GetProperty("type_id").GetString()));

        // 4. 配套的独立骨架运行时包：rigbin + 物理 + 差分/隔离全在里面，根目录带 mod_group.json。
        //    命名必须满足 HD2 独立封包的规则，否则导入端会以「配套项目身份无效」整包拒收。
        var pack = Assert.Single(result.Files.Where(file => file.Kind == "runtime-pack"));
        Assert.Equal("armor-sample.hd2irpack.zip", pack.Path);
        Assert.Equal(pack.Path, result.Report.RuntimePack);
        using var archive = new ZipArchive(new MemoryStream(pack.Data.ToArray()), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("mod_group.json", names);
        Assert.All(names, name => Assert.True(name == "mod_group.json" ||
            name.StartsWith("HD2IndependentRig/", StringComparison.Ordinal), $"{name} 不在 HD2IndependentRig/ 下"));
        var rig = Assert.Single(names, name => name.EndsWith(".rigbin", StringComparison.Ordinal));
        Assert.Matches(@"^HD2IndependentRig/[0-9a-f]{32}\.08241ba732c97a9d\.rigbin$", rig);
        var isolationName = Assert.Single(names, name => name.EndsWith(".isolation.ini", StringComparison.Ordinal));
        Assert.Matches(@"^HD2IndependentRig/owned_[0-9a-f]{24}\.isolation\.ini$", isolationName);
        var differenceName = Assert.Single(names, name => name.EndsWith(".difference.ini", StringComparison.Ordinal));
        Assert.Matches(@"^HD2IndependentRig/hd2free_[0-9a-f]{16}\.difference\.ini$", differenceName);
        Assert.Contains(names, name => name.EndsWith(".hd2phys", StringComparison.Ordinal));
        // 清单文件名必须等于文件内部声明的 ID：游戏按 ID 回查清单。
        var isolationText = Encoding.UTF8.GetString(Read(archive, isolationName));
        var isolationId = Regex.Match(isolationText, @"^isolation_id=(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        Assert.Equal(isolationId + ".isolation.ini", isolationName.Split('/')[^1]);
        var differenceText = Encoding.UTF8.GetString(Read(archive, differenceName));
        var manifestId = Regex.Match(differenceText, @"^manifest_id=(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        Assert.Equal(manifestId + ".difference.ini", differenceName.Split('/')[^1]);

        using var group = JsonDocument.Parse(archive.GetEntry("mod_group.json")!.Open());
        Assert.Equal(GmodRuntimePackBuilder.GroupSchema, group.RootElement.GetProperty("schema").GetString());
        Assert.Matches("^[0-9a-f]{32}$", group.RootElement.GetProperty("id").GetString());
        // exported_at 是导入端的必填校验项（缺了会被判「Mod 组身份、名称或时间无效」）。
        var exportedAt = group.RootElement.GetProperty("exported_at").GetString();
        Assert.True(DateTimeOffset.TryParse(exportedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _), $"exported_at 不是有效时间：{exportedAt}");
        var member = Assert.Single(group.RootElement.GetProperty("members").EnumerateArray().ToArray());
        Assert.Equal(Archive.ToString("x16"), member.GetProperty("archive").GetString());
        Assert.Matches("^[0-9a-f]{32}$", member.GetProperty("project").GetString());
        // 成员清单逐条覆盖包内运行时文件，哈希与内容一致。
        var listed = member.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(names.Where(name => name != "mod_group.json").ToHashSet(StringComparer.Ordinal), listed);
        Assert.Equal("TR-62 Knight", result.Report.Parts[0].TargetArmor);
    }

    private static byte[] Read(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [SampleFact]
    public void NoTargetArmor_WarnsInsteadOfSilentlyUsingInventedIds()
    {
        var sample = RealSourceSample.FindSample()!;
        var binding = new IasArchiveBinding(0x1122334455667788, IasContentDomain.Body,
            [new IasEquipmentBinding(1, [0xDEADBEEF])]);
        using var provider = new GmaAssetProvider(sample);
        var package = SourceModelPackage.Load(provider, "models/shoubi/aes_yz_sb.mdl");
        var result = new GmodIasConverter().Convert(package, new GmodConversionOptions("no-armor", binding,
            PartSplit: GmodPartSplit.None, IncludeSkinSwitch: false, IncludeRawSource: false), provider);

        var warning = Assert.Single(result.Report.Issues.Where(issue => issue.Code == "TargetArmorNotSelected"));
        Assert.Equal(GmodIssueSeverity.Warning, warning.Severity);
        Assert.Empty(result.Report.Parts[0].TargetArmor);
        Assert.Equal(0, result.Report.Parts[0].CleanupUnitCount);
    }

    [SampleFact]
    public void TargetArmorForOneDomain_SkipsTheOtherPartInsteadOfCarryingTheWholeArmour()
    {
        // 只选了身体甲却要拆分时，头盔侧没有可接管的目标：直接跳过并说明，
        // 而不是退化成「接管整套 23 个 Unit × 每个选项」（那样会撞上 512 Unit 上限，整次转换失败）。
        var sample = RealSourceSample.FindSample()!;
        var body = new GmodArmorTarget(Archive, IasContentDomain.Body, "TR-62", "TR-62 Knight", Carrier,
            [Equipment], [Carrier, PlateA, PlateB]);
        var binding = new IasArchiveBinding(Archive, IasContentDomain.Body,
            [new IasEquipmentBinding(Equipment, body.UnitIds)]);

        using var provider = new GmaAssetProvider(sample);
        var package = SourceModelPackage.Load(provider, "models/lc_84/aes_yz_pm.mdl");
        var result = new GmodIasConverter().Convert(package, new GmodConversionOptions("body-only", binding,
            PartSplit: GmodPartSplit.Both, IncludeSkinSwitch: false, IncludeRawSource: false, Targets: [body]),
            provider);

        var part = Assert.Single(result.Report.Parts);
        Assert.Equal("TR-62 Knight", part.TargetArmor);
        Assert.Equal(2, part.CleanupUnitCount);
        var skipped = Assert.Single(result.Report.Issues.Where(issue => issue.Code == "TargetArmorRequiredForPart"));
        Assert.Equal(GmodIssueSeverity.Warning, skipped.Severity);
        Assert.DoesNotContain(result.Files, file => file.Path.StartsWith("helmet/", StringComparison.Ordinal));
        // 每个选项只接管载体 Unit，Unit 总数不随装备闭包膨胀。
        Assert.True(part.UnitCount < 300, $"部位发布 {part.UnitCount} 个 Unit。");
    }
}
