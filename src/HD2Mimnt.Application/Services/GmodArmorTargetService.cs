using HD2Mimnt.ModelConversion.Gmod.Targets;
using HD2Mimnt.ModelConversion.Ias;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 一套可选的目标护甲：游戏内护甲代号 / 名称、IAS 绑定所需的 Archive 与 equipment，
/// 以及整身模型要接管的载体 Unit 与整件护甲的 Unit 闭包。
/// 只含基元类型，UI 与 CLI 都不需要引用模型转换程序集。
/// </summary>
public sealed record GmodIasArmorTarget(
    string Code,
    string Name,
    ulong ArchiveId,
    GmodIasDomain Domain,
    IReadOnlyList<uint> EquipmentKeys,
    ulong CarrierUnitId,
    IReadOnlyList<ulong> UnitIds)
{
    /// <summary>下拉框里的显示文本：代号 + 名称，缺名称时退化为 Archive ID。</summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return $"0x{ArchiveId:x16}";
            if (string.IsNullOrWhiteSpace(Code) || Name.StartsWith(Code, StringComparison.OrdinalIgnoreCase)) return Name;
            return $"{Code} · {Name}";
        }
    }

    public string ArchiveHex => "0x" + ArchiveId.ToString("x16");

    public override string ToString() => DisplayName;

    internal GmodArmorTarget ToModelTarget() => new(ArchiveId,
        Domain == GmodIasDomain.Helmet ? IasContentDomain.Helmet : IasContentDomain.Body,
        Code, Name, CarrierUnitId, EquipmentKeys, UnitIds);
}

/// <summary>
/// 目标护甲目录的加载与查询：读随程序分发的 <c>Config/ArmorTargets.json</c>。
///
/// 目录由 <c>tools/ArmorTargets/build_armor_targets.py</c> 从 HD2SDK 的护甲数据生成，
/// 内容是 Archive ID / equipment key / Unit 闭包等游戏标识，转换器只按用户选择读取，不做猜测。
/// </summary>
public sealed class GmodArmorTargetService
{
    public const string ConfigFileName = "ArmorTargets.json";

    private static readonly Lock Gate = new();
    private static GmodArmorTargetService? _cached;
    private static string? _cachedPath;
    private static DateTime _cachedStamp;

    private readonly IReadOnlyList<GmodArmorTarget> _model;
    private readonly IReadOnlyList<GmodIasArmorTarget> _targets;

    private GmodArmorTargetService(string path, IReadOnlyList<GmodArmorTarget> model)
    {
        Path = path;
        _model = model;
        _targets = model.Select(ToApp).ToArray();
    }

    /// <summary>目录文件的实际路径。</summary>
    public string Path { get; }

    /// <summary>全部目标护甲（身体 + 头盔）。</summary>
    public IReadOnlyList<GmodIasArmorTarget> Targets => _targets;

    public IEnumerable<GmodIasArmorTarget> Bodies => _targets.Where(target => target.Domain == GmodIasDomain.Body);

    public IEnumerable<GmodIasArmorTarget> Helmets => _targets.Where(target => target.Domain == GmodIasDomain.Helmet);

    /// <summary>随程序分发的目录路径（输出目录下的 <c>Config/ArmorTargets.json</c>）。</summary>
    public static string DefaultPath => System.IO.Path.Combine(AppContext.BaseDirectory, "Config", ConfigFileName);

    /// <summary>加载默认目录；文件缺失时返回 null，转换退化为手填 Archive / Native Unit。</summary>
    public static GmodArmorTargetService? TryLoadDefault() =>
        File.Exists(DefaultPath) ? Load(DefaultPath) : null;

    /// <summary>加载指定目录；文件未变化时复用上次解析结果。</summary>
    public static GmodArmorTargetService Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        var stamp = File.GetLastWriteTimeUtc(full);
        lock (Gate)
        {
            if (_cached is not null && _cachedPath is not null &&
                string.Equals(_cachedPath, full, StringComparison.OrdinalIgnoreCase) && _cachedStamp == stamp)
                return _cached;
        }

        var parsed = GmodArmorTargetCatalog.Parse(File.ReadAllBytes(full));
        var service = new GmodArmorTargetService(full, parsed);
        lock (Gate)
        {
            _cached = service;
            _cachedPath = full;
            _cachedStamp = stamp;
        }
        return service;
    }

    /// <summary>按护甲代号、显示名或 Archive ID 查找；找不到返回 null。</summary>
    public GmodIasArmorTarget? Find(string? key)
    {
        var match = GmodArmorTargetCatalog.Find(_model, key);
        return match is null ? null : ToApp(match);
    }

    /// <summary>按 Archive ID 精确匹配，供会话恢复时还原用户选择。</summary>
    public GmodIasArmorTarget? FindByArchive(ulong archiveId) =>
        _targets.FirstOrDefault(target => target.ArchiveId == archiveId);

    private static GmodIasArmorTarget ToApp(GmodArmorTarget target) => new(target.Code, target.Name,
        target.ArchiveId, target.Domain == IasContentDomain.Helmet ? GmodIasDomain.Helmet : GmodIasDomain.Body,
        target.EquipmentKeys, target.CarrierUnitId, target.UnitIds);
}
