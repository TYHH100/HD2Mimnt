using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD2Mimnt.Application.Services;

internal sealed record HellForgeLocalMod(string Id, string Folder);

/// <summary>
/// 直接从 <c>mods\</c> 文件夹读 HellForge 的模组 id，不启动 CLI。
/// 规则与上游 <c>ModCatalog.Load</c> / <c>CosmeticPacks.Parse</c> 一致：
/// <c>hellforge-mod.json</c> 的 <c>id</c>，或 <c>manifest.json</c> 的 <c>Guid</c>（无 Guid 时用名称 slug）。
/// 只用于本地比对（是否已存在、目录在哪），需要“HellForge 是否真的认出来”时仍以 CLI <c>mods</c> 为准。
/// </summary>
internal static class HellForgeLocalMods
{
    public static Dictionary<string, HellForgeLocalMod> Scan(string modsDirectory)
    {
        var mods = new Dictionary<string, HellForgeLocalMod>(StringComparer.Ordinal);
        if (!Directory.Exists(modsDirectory)) return mods;
        foreach (var folder in Directory.EnumerateDirectories(modsDirectory))
        {
            if (Path.GetFileName(folder).StartsWith('.')) continue;
            var id = ReadId(folder);
            if (id != null && !mods.ContainsKey(id)) mods[id] = new HellForgeLocalMod(id, folder);
        }
        return mods;
    }

    private static string? ReadId(string folder)
    {
        var flat = Path.Combine(folder, "hellforge-mod.json");
        if (File.Exists(flat))
        {
            var id = ReadString(flat, "id");
            return IsValidId(id) ? id : null;
        }

        var manifest = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("Guid", out var guid) && guid.ValueKind == JsonValueKind.String
                && Guid.TryParse(guid.GetString(), out var parsed))
                return "pack." + parsed.ToString("D");
            // 没有 Guid 时上游用名称（或目录名）算 slug；面板里没有 Guid 的旧包才走这条。
            if (!root.TryGetProperty("Options", out _)) return null;
            var name = root.TryGetProperty("Name", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : Path.GetFileName(folder);
            return "pack." + Slug(name);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadString(string path, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>上游 ModId 规则：小写字母数字开头，[a-z0-9._-]，最长 64。</summary>
    private static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 64 } && char.IsAsciiLetterOrDigit(id[0])
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    /// <summary>与上游一致的 slug：小写、[a-z0-9-] 之外的字符变 '-'、去首尾 '-'、空则 pack、最长 58。</summary>
    private static string Slug(string? name)
    {
        var builder = new StringBuilder((name ?? string.Empty).Length);
        foreach (var character in (name ?? string.Empty).ToLowerInvariant())
            builder.Append(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? character : '-');
        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0) return "pack";
        return slug.Length > 58 ? slug[..58].TrimEnd('-') : slug;
    }
}

/// <summary>
/// <c>mods\hellforge-mods.json</c> 的本地读写。上游 <c>docs/external-launchers.md</c> 明确允许外部启动器自己写这个文件
/// （“Write the whole file at once, to a temporary file and then a rename”），字段为
/// <c>disabled</c>（关闭的模组 id）、<c>order</c>（顶部在前）、<c>packs</c>（每个外观包的 <c>off</c> 与 <c>sub</c>）。
/// 只改本管理器拥有的 id，其余字段与模组原样保留；内容没变就不落盘（HellForge 的 Mods 页靠文件时间判断是否外部改动）。
/// 为什么不用 <c>mod-switch</c>/<c>mod-pack</c>：这两个命令每次都重新扫描整个 <c>mods\</c>（几十个外观包时约 5 秒一次），
/// 首次同步几十个选项就是几分钟；写状态文件是官方支持的等价路径，且一次写完。
/// </summary>
internal sealed class HellForgeModsFile
{
    private const string FileName = "hellforge-mods.json";
    private const string LegacyFileName = "bts-mods.json";

    private readonly string _directory;
    private readonly string _path;
    private readonly JsonObject _root;
    private readonly List<string> _disabled;
    private readonly List<string> _order;
    private readonly JsonObject _packs;
    private bool _dirty;

    private HellForgeModsFile(string directory, string path, JsonObject root,
        List<string> disabled, List<string> order, JsonObject packs)
    {
        _directory = directory;
        _path = path;
        _root = root;
        _disabled = disabled;
        _order = order;
        _packs = packs;
    }

    public static HellForgeModsFile Load(string modsDirectory)
    {
        var path = Path.Combine(modsDirectory, FileName);
        if (!File.Exists(path))
        {
            var legacy = Path.Combine(modsDirectory, LegacyFileName);
            if (File.Exists(legacy)) path = legacy;
        }

        JsonObject root;
        try
        {
            root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
                ? existing
                : new JsonObject { ["format"] = 1 };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 坏文件按空状态处理：下一次写入会重建成合法的 JSON，HellForge 自己也是这么兜底的。
            root = new JsonObject { ["format"] = 1 };
        }

        var target = Path.Combine(modsDirectory, FileName);
        return new HellForgeModsFile(modsDirectory, target, root,
            ReadStrings(root, "disabled"), ReadStrings(root, "order"),
            GetObject(root, "packs"));
    }

    public IReadOnlyList<string> Order => _order;

    public bool IsDisabled(string id) => _disabled.Contains(id, StringComparer.Ordinal);

    /// <summary>order 是否已经把 <paramref name="ids"/> 按同样顺序放在最前（与 <c>mod-order</c> 的写法一致）。</summary>
    public bool OrderMatches(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return true;
        if (_order.Count < ids.Count) return false;
        for (var i = 0; i < ids.Count; i++)
            if (!string.Equals(_order[i], ids[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>打开这些模组：从 <c>disabled</c> 里移除。</summary>
    public void Enable(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (_disabled.Remove(id)) _dirty = true;
    }

    /// <summary>写一个外观包的选项：<paramref name="off"/> 为关闭的选项名，<paramref name="sub"/> 为变体选择。</summary>
    public void SetPackChoice(string id, IReadOnlyList<string> off, IReadOnlyDictionary<string, string> sub)
    {
        var subs = new JsonObject();
        foreach (var (name, variant) in sub) subs[name] = variant;
        // sub 即使为空也写：HellForge 自己的保存也带这一项，省略会让每次比对都判成“有变化”。
        var node = new JsonObject
        {
            ["off"] = new JsonArray(off.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            ["sub"] = subs
        };
        var existing = _packs[id];
        if (existing != null && JsonNode.DeepEquals(existing, node)) return;
        _packs[id] = node;
        _dirty = true;
    }

    /// <summary>只清掉外观包的选项条目（同一个 id 从外观包改成了扁平导出）。</summary>
    public void ForgetPack(string id)
    {
        if (_packs.Remove(id)) _dirty = true;
    }

    /// <summary>清掉这些 id 的开关、排序与外观包选择（模组目录已被删除）。</summary>
    public void Remove(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (_disabled.Remove(id)) _dirty = true;
            if (_order.RemoveAll(item => string.Equals(item, id, StringComparison.Ordinal)) > 0) _dirty = true;
            if (_packs.Remove(id)) _dirty = true;
        }
    }

    /// <summary>原子落盘（临时文件 + 改名）；没有改动时不动文件。</summary>
    public bool SaveIfChanged()
    {
        if (!_dirty) return false;
        SetProperty(_root, "format", GetProperty(_root, "format") ?? 1);
        SetProperty(_root, "disabled", ToArray(_disabled));
        SetProperty(_root, "order", ToArray(_order));
        if (_packs.Count > 0) SetProperty(_root, "packs", _packs);
        else RemoveProperty(_root, "packs");

        Directory.CreateDirectory(_directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temporary, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        _dirty = false;
        return true;
    }

    private static List<string> ReadStrings(JsonObject root, string property)
    {
        var values = new List<string>();
        if (GetProperty(root, property) is not JsonArray array) return values;
        foreach (var item in array)
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text)
                && !values.Contains(text, StringComparer.Ordinal))
                values.Add(text);
        return values;
    }

    /// <summary>按名字取字段，大小写不敏感（上游写小写，历史文件/其他工具可能写成 PascalCase）。</summary>
    private static JsonNode? GetProperty(JsonObject root, string name)
    {
        foreach (var (key, value) in root)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static JsonObject GetObject(JsonObject root, string name) =>
        GetProperty(root, name) is JsonObject value ? value : new JsonObject();

    /// <summary>写字段：先删掉大小写不同的同名键，避免文件里出现两份。</summary>
    private static void SetProperty(JsonObject root, string name, JsonNode value)
    {
        RemoveProperty(root, name);
        root[name] = value;
    }

    private static void RemoveProperty(JsonObject root, string name)
    {
        foreach (var key in root.Select(pair => pair.Key)
                     .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToList())
            root.Remove(key);
    }

    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
