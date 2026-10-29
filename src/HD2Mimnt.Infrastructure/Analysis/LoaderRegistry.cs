using System.Text.Json;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>
/// Lua 加载器识别与策略配置。识别特征（GUID、入口资源、启动正文标记、注册模块、依赖声明）与
/// 排序/互斥策略全部来自 <c>LoaderCatalog.json</c>（随应用分发的 <c>Config/LoaderCatalog.json</c> 默认，
/// 可用 <c>&lt;AppData&gt;/loaders.json</c> 覆盖），新增加载器不再需要修改代码。快照不可变，识别线程始终读取同一个完整配置。
/// </summary>
public static class LoaderRegistry
{
    /// <summary>启动资源正文标记检查：资源正文须同时包含全部标记（LuaJIT 字符串常量保留，不执行脚本）。</summary>
    public sealed class StartupCheck
    {
        public required string Resource { get; init; }
        public required ulong Hash { get; init; }
        public required byte[][] Markers { get; init; }
    }

    public sealed class LoaderDefinition
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required BslModRole Role { get; init; }
        public required int Priority { get; init; }
        public string? MutexGroup { get; init; }
        /// <summary>从属加载器：需由该 id 的主加载器托管才能与其它加载器共存（如 Chef Shared Loader 由 Boot Mod Loader 启动）。</summary>
        public string? GuestOf { get; init; }
        public required Guid[] Guids { get; init; }
        public ulong[] EntryHashes { get; init; } = [];
        public StartupCheck[] StartupChecks { get; init; } = [];
        public ulong[] RegisteredModuleHashes { get; init; } = [];
        public Regex[] RequiresPatterns { get; init; } = [];
        public string[] DependencyNames { get; init; } = [];
        public string[] DependencyKeys { get; init; } = [];
    }

    /// <summary>一份完整配置的只读快照；替换时整体原子切换，扫描线程不会读到混合配置。</summary>
    public sealed class Snapshot
    {
        public required int RuleVersion { get; init; }
        public required int DependencyPriority { get; init; }
        public required LoaderDefinition[] Loaders { get; init; }
        public required string[] AddonExcludes { get; init; }
        public required IReadOnlyDictionary<Guid, LoaderDefinition> ByGuid { get; init; }
        public required IReadOnlyDictionary<ulong, LoaderDefinition> ByEntryHash { get; init; }
        public required IReadOnlyDictionary<ulong, List<(LoaderDefinition Definition, StartupCheck Check)>> ByStartupHash { get; init; }
        public required IReadOnlyDictionary<ulong, LoaderDefinition> ByRegisteredModuleHash { get; init; }
        public required Regex[] RequiresPatterns { get; init; }
        public required HashSet<string> DependencyNames { get; init; }
        public required HashSet<Guid> DependencyGuids { get; init; }
        public required HashSet<string> DependencyKeys { get; init; }
        public required HashSet<BslModRole> MutexRoles { get; init; }
        /// <summary>从属加载器角色 → 托管它的主加载器角色。</summary>
        public required IReadOnlyDictionary<BslModRole, BslModRole> GuestHosts { get; init; }
        /// <summary>主互斥组加载器显示名，按配置顺序（如 “BSL / MDL / Boot Mod Loader”）。</summary>
        public required string LoaderNames { get; init; }
        /// <summary>从属加载器显示名（如 “Chef Shared Loader”）。</summary>
        public required string GuestLoaderNames { get; init; }
        /// <summary>托管从属加载器的主加载器显示名（如 “Boot Mod Loader”）。</summary>
        public required string HostLoaderNames { get; init; }
        /// <summary>末位部署槽位（SmoothBoot 类）加载器显示名。</summary>
        public required string SmoothBootNames { get; init; }

        public LoaderDefinition? DefinitionOf(Guid? guid)
            => guid is { } value && ByGuid.TryGetValue(value, out var definition) ? definition : null;

        /// <summary>配置槽位的部署排序权重；配置已移除的历史角色按主加载器段兜底，不能退回普通模组段。</summary>
        public int PriorityRank(BslModRole role)
        {
            if (role == BslModRole.None) return 0;
            if (role == BslModRole.Dependency) return DependencyPriority;
            var ranks = Loaders.Where(loader => loader.Role == role).Select(loader => loader.Priority).ToList();
            if (ranks.Count > 0) return ranks.Min();
            return Loaders.Where(loader => loader.MutexGroup != null).Select(loader => loader.Priority).DefaultIfEmpty(2).Min();
        }

        /// <summary>受管理的加载器（主互斥组成员或从属加载器），部署时固定在配置页末段。</summary>
        public bool IsLoader(BslModRole role) => MutexRoles.Contains(role) || GuestHosts.ContainsKey(role);

        /// <summary>主互斥组成员：彼此只能启用一个。</summary>
        public bool IsPrimary(BslModRole role) => MutexRoles.Contains(role);

        public bool IsGuest(BslModRole role) => GuestHosts.ContainsKey(role);

        public bool CanHost(BslModRole host, BslModRole guest)
            => GuestHosts.TryGetValue(guest, out var value) && value == host;

        public string DisplayNameOf(BslModRole role)
            => Loaders.FirstOrDefault(loader => loader.Role == role)?.DisplayName ?? role.ToString();
    }

    private static Snapshot _snapshot = BuildSnapshot(ReadDistributed());

    /// <summary>外部覆盖文件解析失败时的原因；null 表示配置加载正常。</summary>
    public static string? InitializationError { get; private set; }

    public static Snapshot Current => _snapshot;

    /// <summary>使用 <paramref name="appDataDirectory"/> 下的 <c>loaders.json</c> 覆盖分发默认；文件缺失用默认，解析失败回退默认并记录原因。</summary>
    public static void Initialize(string? appDataDirectory)
    {
        InitializationError = null;
        if (string.IsNullOrWhiteSpace(appDataDirectory)) return;
        var path = Path.Combine(appDataDirectory, "loaders.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = JsonDocument.Parse(File.ReadAllText(path));
            using (json)
            {
                _snapshot = BuildSnapshot(json.RootElement);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or IOException or UnauthorizedAccessException)
        {
            InitializationError = ex.Message;
            _snapshot = BuildSnapshot(ReadDistributed());
        }
    }

    /// <summary>丢弃外部覆盖并恢复分发默认；测试隔离用。</summary>
    public static void ResetToDefault() => _snapshot = BuildSnapshot(ReadDistributed());

    private const string CatalogFileName = "LoaderCatalog.json";

    private static JsonElement ReadDistributed()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Config", CatalogFileName);
        if (!File.Exists(path)) throw new InvalidOperationException($"缺少随应用分发的加载器配置 {path}");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        // RootElement 只在 document 生存期内有效，克隆后脱离 using 供 BuildSnapshot 使用。
        return document.RootElement.Clone();
    }

    private static Snapshot BuildSnapshot(JsonElement root)
    {
        var version = RequiredInt(root, "ruleVersion");
        var dependencyPriority = root.TryGetProperty("dependencyPriority", out var dependency) && dependency.ValueKind == JsonValueKind.Number
            ? dependency.GetInt32() : 1;
        var addonExcludes = root.TryGetProperty("addonExcludes", out var excludes) && excludes.ValueKind == JsonValueKind.Array
            ? excludes.EnumerateArray().Select(item => item.GetString() ?? "").Where(name => name.Length > 0).ToArray()
            : [];

        var loaders = new List<LoaderDefinition>();
        foreach (var element in root.GetProperty("loaders").EnumerateArray()) loaders.Add(ParseLoader(element));
        var definitions = loaders.ToArray();

        var byGuid = new Dictionary<Guid, LoaderDefinition>();
        var byEntry = new Dictionary<ulong, LoaderDefinition>();
        var byStartup = new Dictionary<ulong, List<(LoaderDefinition, StartupCheck)>>();
        var byModule = new Dictionary<ulong, LoaderDefinition>();
        var requires = new List<Regex>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var guids = new HashSet<Guid>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mutexRoles = new HashSet<BslModRole>();
        var guestHosts = new Dictionary<BslModRole, BslModRole>();
        var byId = definitions.ToDictionary(loader => loader.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            foreach (var guid in definition.Guids) byGuid[guid] = definition;
            foreach (var hash in definition.EntryHashes) byEntry[hash] = definition;
            foreach (var hash in definition.RegisteredModuleHashes) byModule.TryAdd(hash, definition);
            foreach (var check in definition.StartupChecks)
            {
                if (!byStartup.TryGetValue(check.Hash, out var list)) byStartup[check.Hash] = list = [];
                list.Add((definition, check));
            }
            requires.AddRange(definition.RequiresPatterns);
            foreach (var name in definition.DependencyNames) names.Add(name);
            foreach (var guid in definition.Guids) guids.Add(guid);
            foreach (var key in definition.DependencyKeys) keys.Add(key);
            if (definition.MutexGroup != null) mutexRoles.Add(definition.Role);
        }
        foreach (var definition in definitions.Where(loader => loader.GuestOf != null))
        {
            if (!byId.TryGetValue(definition.GuestOf!, out var host) || host.MutexGroup == null)
                throw new InvalidOperationException($"加载器 {definition.Id} 的 guestOf 指向未知或非互斥加载器: {definition.GuestOf}");
            guestHosts[definition.Role] = host.Role;
        }
        var hostRoles = guestHosts.Values.ToHashSet();

        return new Snapshot
        {
            RuleVersion = version,
            DependencyPriority = dependencyPriority,
            Loaders = definitions,
            AddonExcludes = addonExcludes,
            ByGuid = byGuid,
            ByEntryHash = byEntry,
            ByStartupHash = byStartup,
            ByRegisteredModuleHash = byModule,
            RequiresPatterns = [.. requires],
            DependencyNames = names,
            DependencyGuids = guids,
            DependencyKeys = keys,
            MutexRoles = mutexRoles,
            GuestHosts = guestHosts,
            LoaderNames = string.Join(" / ", definitions.Where(item => item.MutexGroup != null).Select(item => item.DisplayName)),
            GuestLoaderNames = string.Join(" / ", definitions.Where(item => item.GuestOf != null).Select(item => item.DisplayName)),
            HostLoaderNames = string.Join(" / ", definitions.Where(item => hostRoles.Contains(item.Role)).Select(item => item.DisplayName)),
            SmoothBootNames = string.Join(" / ", definitions.Where(item => item.Role == BslModRole.SmoothBoot).Select(item => item.DisplayName))
        };
    }

    private static LoaderDefinition ParseLoader(JsonElement element)
    {
        var roleText = RequiredString(element, "role");
        var role = roleText switch
        {
            // 与 BslModRole 枚举槽位一一对应；新加载器默认用 loader 槽位，特殊部署位置再加新槽位。
            "loader" => BslModRole.Loader,
            "mdl" => BslModRole.MdlLoader,
            "bml" => BslModRole.BmlLoader,
            "csl" => BslModRole.CslLoader,
            "smoothboot" => BslModRole.SmoothBoot,
            _ => throw new InvalidOperationException($"加载器配置的 role 未知: {roleText}")
        };
        var guids = StringArray(element, "guids").Select(text => Guid.Parse(text)).ToArray();
        var startupChecks = Array.Empty<StartupCheck>();
        if (element.TryGetProperty("startupChecks", out var checks) && checks.ValueKind == JsonValueKind.Array)
            startupChecks = checks.EnumerateArray().Select(check => new StartupCheck
            {
                Resource = RequiredString(check, "resource"),
                Hash = BslModDetector.ResourceHash(RequiredString(check, "resource")),
                Markers = check.GetProperty("markers").EnumerateArray()
                    .Select(marker => System.Text.Encoding.UTF8.GetBytes(marker.GetString() ?? ""))
                    .Where(marker => marker.Length > 0).ToArray()
            }).Where(check => check.Markers.Length > 0).ToArray();
        return new LoaderDefinition
        {
            Id = RequiredString(element, "id"),
            DisplayName = RequiredString(element, "displayName"),
            Role = role,
            Priority = element.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number ? priority.GetInt32() : 2,
            MutexGroup = element.TryGetProperty("mutexGroup", out var mutex) && mutex.ValueKind == JsonValueKind.String ? mutex.GetString() : null,
            GuestOf = element.TryGetProperty("guestOf", out var guest) && guest.ValueKind == JsonValueKind.String ? guest.GetString() : null,
            Guids = guids,
            EntryHashes = StringArray(element, "entryResources").Select(BslModDetector.ResourceHash).ToArray(),
            StartupChecks = startupChecks,
            RegisteredModuleHashes = StringArray(element, "registeredModules").Select(BslModDetector.ResourceHash).ToArray(),
            RequiresPatterns = StringArray(element, "requiresPatterns")
                .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))).ToArray(),
            DependencyNames = StringArray(element, "dependencyNames"),
            DependencyKeys = StringArray(element, "dependencyKeys")
        };
    }

    private static string RequiredString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length > 0
            ? text : throw new InvalidOperationException($"加载器配置缺少字段 {name}");

    private static int RequiredInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : throw new InvalidOperationException($"加载器配置缺少字段 {name}");

    private static string[] StringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? "").Where(text => text.Length > 0).ToArray()
            : [];
}
