using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.IO;
using HD2Mimnt.ModelConversion.Gmod;
using HD2Mimnt.ModelConversion.Gmod.Parts;
using HD2Mimnt.ModelConversion.Gmod.Targets;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Mdl;

namespace HD2Mimnt.Application.Services;

/// <summary>应用层稳定的内容域枚举，避免 UI/CLI 直接依赖模型转换程序集。</summary>
public enum GmodIasDomain { Body, Helmet }

/// <summary>应用层稳定的开关发布模式枚举。</summary>
public enum GmodIasSwitchMode { Auto, Independent, Composite }

/// <summary>
/// 身体/头盔拆分方式。HD2 把身体护甲与头盔作为两套 IAS 包装备，
/// 而 GMOD 的 MDL 把头与身体放在一起，默认按骨骼权重拆成两包。
/// </summary>
public enum GmodIasPartSplit { Both, Body, Helmet, None }

public sealed record GmodIasConversionRequest(
    string AddonPath,
    string ModelPath,
    string OutputDirectory,
    string ProjectId,
    ulong ArchiveId,
    GmodIasDomain Domain,
    uint EquipmentKey,
    IReadOnlyList<ulong> NativeUnitIds,
    GmodIasSwitchMode SwitchMode = GmodIasSwitchMode.Auto,
    bool IncludeSkinSwitch = true,
    bool IncludePhysics = true,
    bool IncludeRawSource = true,
    GmodIasPartSplit PartSplit = GmodIasPartSplit.Both,
    ulong? HelmetArchiveId = null,
    IReadOnlyList<string>? ExposedSwitches = null,
    int MaxSwitchOptions = 256,
    GmodIasArmorTarget? BodyArmor = null,
    GmodIasArmorTarget? HelmetArmor = null);

public sealed record GmodIasConversionIssue(string Code, string Asset, string Message, string Severity);

/// <summary>单部位（身体 / 头盔）统计，供界面与 CLI 按包展示。</summary>
public sealed record GmodIasPartSummary(
    string Part,
    string Domain,
    string Archive,
    int MeshCount,
    int TriangleCount,
    int SwitchCount,
    int OptionCount,
    int PhysicsProfileCount,
    int UnitCount = 0,
    int MaterialResourceCount = 0,
    int TextureResourceCount = 0,
    long PackageBytes = 0,
    string TargetArmor = "",
    int CleanupUnitCount = 0);

/// <summary>只含基元类型与应用层枚举的转换摘要，供 UI/CLI 展示，避免展示层引用格式程序集。</summary>
public sealed record GmodIasConversionSummary(
    string ProjectId,
    string ModelPath,
    int BoneCount,
    int MeshCount,
    int VertexCount,
    int TriangleCount,
    int MaterialCount,
    int TextureCount,
    int DecodedTextureCount,
    int SwitchCount,
    int OptionCount,
    int PhysicsChainCount,
    int PhysicsJointCount,
    int PhysicsProfileCount,
    int RigProfileCount,
    int RawSourceFileCount,
    IReadOnlyList<GmodIasConversionIssue> Issues,
    IReadOnlyList<GmodIasPartSummary> Parts,
    string RuntimePack = "",
    long RuntimePackBytes = 0);

public sealed record GmodIasConversionOutcome(string OutputDirectory, int FileCount, GmodIasConversionSummary Report);

/// <summary>
/// addon 内单个 Source 模型的只读概况。<see cref="IsConvertible"/> 为 false 表示缺少 VVD/VTX，
/// 该模型无法完成转换，但仍会出现在分析结果里供用户排查。
/// </summary>
public sealed record GmodAddonModel(
    string Path,
    string Name,
    int BoneCount,
    int MeshCount,
    int MaterialCount,
    int BodyGroupCount,
    int BodyGroupOptionCount,
    int SkinCount,
    int JiggleBoneCount,
    bool HasVertexData,
    bool HasIndexData,
    bool HasPhysics,
    IReadOnlyList<GmodBodyGroupInfo> BodyGroups)
{
    public bool IsConvertible => HasVertexData && HasIndexData;
}

/// <summary>一个 $bodygroup 的编号、名称与备选，键为 <c>bodygroup:N</c>，用于挑选要发布为 IAS 开关的组。</summary>
public sealed record GmodBodyGroupInfo(int Index, string Name, IReadOnlyList<string> Options);

/// <summary>addon 扫描结果：可直接用于模型下拉选择的候选清单与逐模型诊断。</summary>
public sealed record GmodAddonAnalysis(
    string AddonPath,
    IReadOnlyList<GmodAddonModel> Models,
    IReadOnlyList<GmodIasConversionIssue> Issues);

/// <summary>
/// 应用层编排：从 GMA 或解压目录读取 GMOD 模型，转换成 HD2 IAS 转换包并整体写入输出目录。
/// 业务与格式细节全部交给 <c>HD2Mimnt.ModelConversion</c>，本服务只负责资源生命周期与落盘。
/// </summary>
public sealed class GmodIasConversionService
{
    public async Task<GmodIasConversionOutcome> ConvertAsync(GmodIasConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        // 选了目标护甲就由目录提供 Archive / equipment / Unit 闭包；手填字段只在没选护甲的部位生效。
        if ((request.BodyArmor ?? request.HelmetArmor) is { } seed)
        {
            request = request with
            {
                ArchiveId = request.ArchiveId != 0 ? request.ArchiveId : seed.ArchiveId,
                EquipmentKey = request.EquipmentKey != 0 ? request.EquipmentKey : seed.EquipmentKeys[0],
                NativeUnitIds = request.NativeUnitIds.Count > 0 ? request.NativeUnitIds : seed.UnitIds
            };
        }
        if (string.IsNullOrWhiteSpace(request.AddonPath) || string.IsNullOrWhiteSpace(request.ModelPath) ||
            string.IsNullOrWhiteSpace(request.OutputDirectory))
            throw new ArgumentException("addon、模型路径和输出目录都不能为空。");
        if (request.ArchiveId == 0 || request.EquipmentKey == 0 || request.NativeUnitIds.Count == 0 ||
            request.NativeUnitIds.Any(id => id == 0) || request.NativeUnitIds.Distinct().Count() != request.NativeUnitIds.Count)
            throw new ArgumentException("Archive ID、Equipment Key 与 Native Unit ID 必须非零且互不重复。");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory));
        Directory.CreateDirectory(root);

        var domain = request.Domain == GmodIasDomain.Helmet ? IasContentDomain.Helmet : IasContentDomain.Body;
        var mode = request.SwitchMode switch
        {
            GmodIasSwitchMode.Independent => GmodSwitchMode.Independent,
            GmodIasSwitchMode.Composite => GmodSwitchMode.Composite,
            _ => GmodSwitchMode.Auto
        };
        using ISourceAssetProvider provider = CreateProvider(request.AddonPath, cancellationToken);
        var binding = new IasArchiveBinding(request.ArchiveId, domain,
            [new IasEquipmentBinding(request.EquipmentKey, request.NativeUnitIds)]);
        var targets = new List<GmodArmorTarget>();
        if (request.BodyArmor is not null) targets.Add(RequireDomain(request.BodyArmor, GmodIasDomain.Body));
        if (request.HelmetArmor is not null) targets.Add(RequireDomain(request.HelmetArmor, GmodIasDomain.Helmet));
        var options = new GmodConversionOptions(request.ProjectId, binding,
            SwitchMode: mode, IncludeSkinSwitch: request.IncludeSkinSwitch,
            IncludePhysics: request.IncludePhysics, IncludeRawSource: request.IncludeRawSource,
            PartSplit: request.PartSplit switch
            {
                GmodIasPartSplit.Body => GmodPartSplit.Body,
                GmodIasPartSplit.Helmet => GmodPartSplit.Helmet,
                GmodIasPartSplit.None => GmodPartSplit.None,
                _ => GmodPartSplit.Both
            },
            HelmetArchiveId: request.HelmetArchiveId,
            ExposedAxes: request.ExposedSwitches,
            MaxSwitchOptions: request.MaxSwitchOptions,
            Targets: targets.Count == 0 ? null : targets.AsReadOnly());

        var result = await new GmodIasConverter().ConvertAsync(provider, request.ModelPath, options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var file in result.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = PathGuard.ResolveInsideRoot(root, Path.Combine(root, file.Path),
                $"转换包成员越界：{file.Path}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, file.Data.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        return new GmodIasConversionOutcome(root, result.Files.Count, Summarize(result.Report));
    }

    private static GmodIasConversionSummary Summarize(GmodConversionReport report) => new(
        report.ProjectId, report.ModelPath, report.BoneCount, report.MeshCount, report.VertexCount,
        report.TriangleCount, report.MaterialCount, report.TextureCount, report.DecodedTextureCount,
        report.SwitchCount, report.OptionCount, report.PhysicsChainCount, report.PhysicsJointCount,
        report.PhysicsProfileCount, report.RigProfileCount, report.RawSourceFileCount,
        report.Issues.Select(issue => new GmodIasConversionIssue(issue.Code, issue.Asset,
            issue.Message, issue.Severity.ToString())).ToArray(),
        report.Parts.Select(part => new GmodIasPartSummary(part.Part, part.Domain, part.Archive,
            part.MeshCount, part.TriangleCount, part.SwitchCount, part.OptionCount,
            part.PhysicsProfileCount, part.UnitCount, part.MaterialResourceCount, part.TextureResourceCount,
            part.PackageBytes, part.TargetArmor, part.CleanupUnitCount)).ToArray(),
        report.RuntimePack, report.RuntimePackBytes);

    /// <summary>目标护甲的内容域必须和它在本次转换里扮演的角色一致，否则绑定会悄悄错位。</summary>
    private static GmodArmorTarget RequireDomain(GmodIasArmorTarget target, GmodIasDomain expected) =>
        target.Domain == expected
            ? target.ToModelTarget()
            : throw new ArgumentException(
                $"目标护甲 {target.DisplayName} 属于 {target.Domain} 域，不能用于 {expected} 部位。");

    /// <summary>从模型文件名生成安全的 IAS 项目 ID（1–63 位 ASCII）。</summary>
    public static string DeriveProjectId(string modelPath)
    {
        var stem = Path.GetFileNameWithoutExtension(modelPath.Replace('\\', '/'));
        var safe = new string(stem.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_').ToArray())
            .Trim('.', '_');
        return string.IsNullOrEmpty(safe) ? "gmod-model" : safe[..Math.Min(63, safe.Length)];
    }

    /// <summary>未提供 Archive ID 时，按项目与模型路径生成稳定的非零 64 位 ID。</summary>
    public static ulong DeriveArchiveId(string projectId, string modelPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "|" + modelPath));
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash);
        return value == 0 ? 0x9E3779B97F4A7C15UL : value;
    }

    /// <summary>开关键：<c>skin</c> 或 <c>bodygroup:编号</c>，用于挑选真正要发布为 IAS 开关的组。</summary>
    public static bool IsSwitchKey(string value)
    {
        if (string.Equals(value, "skin", StringComparison.OrdinalIgnoreCase)) return true;
        const string prefix = "bodygroup:";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0;
    }

    /// <summary>解析逗号分隔的开关键列表；含非法键时返回 false。</summary>
    public static bool TryParseSwitchKeys(string? value, out IReadOnlyList<string> keys)
    {
        keys = [];
        var parsed = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parsed.Length == 0 || parsed.Any(key => !IsSwitchKey(key))) return false;
        keys = parsed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    /// <summary>解析可带 0x 前缀的十六进制 ID；空串返回 false，由调用方使用默认值。</summary>
    public static bool TryParseHexId(string? value, out ulong id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>解析逗号分隔的 Native Unit ID 列表；空串返回 false。</summary>
    public static bool TryParseNativeIds(string? value, out IReadOnlyList<ulong> ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parsed = new List<ulong>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseHexId(item, out var id) || id == 0) return false;
            parsed.Add(id);
        }
        if (parsed.Distinct().Count() != parsed.Count) return false;
        ids = parsed.AsReadOnly();
        return true;
    }

    /// <summary>addon 内最多分析的模型数量，超出部分只记录诊断，避免超大 addon 卡住界面。</summary>
    public const int MaxAnalyzedModels = 512;

    private static readonly string[] VtxSuffixes = [".dx90.vtx", ".dx80.vtx", ".sw.vtx"];

    /// <summary>
    /// 扫描 addon（GMA 或已解压目录）内的全部 .mdl，解析头部与骨骼/网格/开关表，
    /// 供界面在选定 addon 后直接给出可转换模型下拉列表。
    /// </summary>
    public Task<GmodAddonAnalysis> AnalyzeAsync(string addonPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(addonPath))
            throw new ArgumentException("addon 路径不能为空。", nameof(addonPath));
        var path = addonPath.Trim();
        return Task.Run(() => Analyze(path, cancellationToken), cancellationToken);
    }

    private static GmodAddonAnalysis Analyze(string addonPath, CancellationToken cancellationToken)
    {
        using var provider = CreateProvider(addonPath, cancellationToken);
        var files = new HashSet<string>(provider.EnumeratePaths(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var models = new List<GmodAddonModel>();
        var issues = new List<GmodIasConversionIssue>();
        foreach (var path in files
                     .Where(file => file.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (models.Count >= MaxAnalyzedModels)
            {
                issues.Add(new GmodIasConversionIssue("AnalysisTruncated", path,
                    $"只分析前 {MaxAnalyzedModels} 个模型，其余 .mdl 已跳过。", "Warning"));
                break;
            }
            if (!provider.TryRead(path, out var data, cancellationToken)) continue;
            try
            {
                models.Add(Describe(path, data, files));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                issues.Add(new GmodIasConversionIssue("UnreadableModel", path, error.Message, "Warning"));
            }
        }

        models.Sort(CompareModels);
        return new GmodAddonAnalysis(addonPath, models.AsReadOnly(), issues.AsReadOnly());
    }

    private static GmodAddonModel Describe(string path, byte[] data, HashSet<string> files)
    {
        var mdl = MdlFile.Parse(data, path);
        var stem = path[..^4];
        return new GmodAddonModel(
            path,
            Path.GetFileNameWithoutExtension(path),
            mdl.Bones.Count,
            mdl.BodyParts.Sum(part => part.Models.Sum(model => model.Meshes.Count)),
            mdl.Textures.Count,
            mdl.BodyParts.Count(part => part.Models.Count > 1),
            mdl.BodyParts.Sum(part => part.Models.Count),
            Math.Max(1, mdl.SkinFamilies.Count),
            mdl.Bones.Count(bone => bone.Jiggle is not null),
            files.Contains(stem + ".vvd"),
            VtxSuffixes.Any(suffix => files.Contains(stem + suffix)),
            files.Contains(stem + ".phy"),
            mdl.BodyParts.Select(part => new GmodBodyGroupInfo(part.Index, part.Name,
                part.Models.Select(model => model.Name).ToArray())).ToArray());
    }

    /// <summary>排序：玩家模型优先，其次可转换、网格更多、路径字典序，便于默认选中主模型。</summary>
    private static int CompareModels(GmodAddonModel left, GmodAddonModel right)
    {
        var byPlayer = Rank(left).CompareTo(Rank(right));
        if (byPlayer != 0) return byPlayer;
        var byConvertible = right.IsConvertible.CompareTo(left.IsConvertible);
        if (byConvertible != 0) return byConvertible;
        var byMeshes = right.MeshCount.CompareTo(left.MeshCount);
        if (byMeshes != 0) return byMeshes;
        return string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(GmodAddonModel model) =>
        model.Path.Contains("models/player/", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static ISourceAssetProvider CreateProvider(string addonPath, CancellationToken cancellationToken)    {
        if (Directory.Exists(addonPath)) return new DirectoryAssetProvider(addonPath, cancellationToken);
        if (!File.Exists(addonPath)) throw new FileNotFoundException("找不到 GMOD addon 文件或目录。", addonPath);
        if (!addonPath.EndsWith(".gma", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("单个文件输入必须是 .gma addon；已解压的素材请传目录。");
        return new GmaAssetProvider(addonPath, cancellationToken);
    }
}
