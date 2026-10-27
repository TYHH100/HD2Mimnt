using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>只读包内声明及 Lua 索引/入口，不执行脚本，不按显示名称或补丁前缀猜测。
/// 识别特征来自 LoaderRegistry 配置；规则版本提升后旧缓存按需重扫。</summary>
public static partial class BslModDetector
{
    // 以下 GUID 与随应用分发的 Config/LoaderCatalog.json 同步，仅供既有调用方/测试引用；以配置为准。
    public static readonly Guid LoaderGuid = new("612eaf70-d682-43c7-9efd-16dcc695f977");
    public static readonly Guid MdlGuid = new("761188b2-c45b-4607-9241-f89ca221c47b");
    public static readonly Guid SmoothBootGuid = new("7c3e9a52-1b84-4f6a-9c2d-8e5f0a1b2c3d");
    public static readonly Guid BootModLoaderGuid = new("3f7a2c91-6d4e-4b58-9a0c-e15b82d4f6a7");

    public static int DetectionVersion => LoaderRegistry.Current.RuleVersion;
    public static BslModRole? KnownRole(Guid? guid) => LoaderRegistry.Current.DefinitionOf(guid)?.Role;

    private const ulong LuaType = 0xa14e8dfa2cd117e2;

    [GeneratedRegex(@"^-- HD2-Addon: (mods/[A-Za-z0-9_]+(?:/[A-Za-z0-9_]+)*)\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex AddonPattern();

    public static Task<BslModRole?> DetectAsync(Mod mod, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var role = Detect(mod, cancellationToken);
            // 正常取消直接完成为 Canceled，避免扫描循环主动抛异常触发调试器中断。
            // 不能把取消返回的 null 当作识别成功；调用方 await 时仍收到取消异常。
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<BslModRole?>(cancellationToken)
                : Task.FromResult(role);
        }, cancellationToken);

    private static BslModRole? Detect(Mod mod, CancellationToken cancellationToken)
    {
        // 全程使用同一份配置快照，避免扫描中途配置替换造成混合判断。
        var snapshot = LoaderRegistry.Current;
        if (cancellationToken.IsCancellationRequested) return null;
        if (snapshot.DefinitionOf(mod.Manifest?.Guid) is { } knownRole) return knownRole.Role;
        var declaredDependency = snapshot.RequiresPatterns.Any(pattern => pattern.IsMatch(mod.Manifest?.Description ?? mod.Description));
        if (!Directory.Exists(mod.Path)) return declaredDependency ? BslModRole.Dependency : null;

        // 官方发行包的 *-manifest.json 是 provenance，requires 不在管理器 manifest 的 Options 中。
        foreach (var path in Directory.EnumerateFiles(mod.Path, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (cancellationToken.IsCancellationRequested) return null;
            if (!Path.GetFileName(path).EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024 * 1024) continue;
            using var doc = Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                ? JsonDocument.Parse(ManifestPackage.Read(path, _ => { }).ToJsonString())
                : JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            // 特殊身份优先于 Description 中的通用依赖声明，不受 JSON 字段顺序影响。
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Name.Equals("Guid", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var guid) && snapshot.DefinitionOf(guid) is { } packageRole)
                    return packageRole.Role;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("Description", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && snapshot.RequiresPatterns.Any(pattern => pattern.IsMatch(property.Value.GetString() ?? ""))) declaredDependency = true;
                if ((property.Name.Equals("requires", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
                    && DeclaresLoader(property.Value, snapshot)) declaredDependency = true;
            }
        }

        var registered = false;
        var ownsStartup = false;
        BslModRole? startupRole = null;
        Span<byte> header = stackalloc byte[72];
        Span<byte> entry = stackalloc byte[80];
        foreach (var path in Directory.EnumerateFiles(mod.Path, "*.patch_*", SearchOption.AllDirectories))
        {
            if (cancellationToken.IsCancellationRequested) return null;
            var extension = Path.GetExtension(path);
            if (!extension.StartsWith(".patch_", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(extension.AsSpan(7), out _)) continue;
            using var stream = File.OpenRead(path);
            if (stream.Length < 4) continue;
            stream.ReadExactly(header[..4]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xF0000011) continue;
            stream.ReadExactly(header[4..]);
            var start = 72L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) * 32L;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            if (start + count * 80L > stream.Length) throw new InvalidDataException("BSL 资源索引不完整");
            for (long index = 0; index < count; index++)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                stream.Position = start + index * 80;
                stream.ReadExactly(entry);
                if (BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]) != LuaType) continue;
                var name = BinaryPrimitives.ReadUInt64LittleEndian(entry);
                // 配置声明启动资源的包一律视为自带启动脚本；是否是该加载器本体仍须正文标记确认。
                var startupChecks = snapshot.ByStartupHash.TryGetValue(name, out var checks) ? checks : null;
                ownsStartup |= startupChecks != null;
                registered |= snapshot.ByRegisteredModuleHash.ContainsKey(name);
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
                if (offset > (ulong)stream.Length || size > (ulong)stream.Length - offset)
                    throw new InvalidDataException("BSL Lua 资源越界");
                if (size < 8) continue;
                stream.Position = (long)offset;
                var prefix = new byte[Math.Min(size, 264u)];
                stream.ReadExactly(prefix);
                if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4)) != 2) continue;
                var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
                if (bodyLength > size - 8) throw new InvalidDataException("BSL Lua 内容不完整");
                // GUID 缺失/变化时，仍以独有 Lua 资源身份兜底；首先校验索引与 Lua envelope。
                if (snapshot.ByEntryHash.TryGetValue(name, out var entryDefinition)) return entryDefinition.Role;
                if (startupChecks != null)
                {
                    // 启动资源正文须同时包含全部配置标记，避免把其它 boot/Wwise 替换当作加载器。
                    // LuaJIT 编译后的字符串常量也保留这些标记，不执行或反编译脚本。
                    var body = new byte[Math.Min(bodyLength, 8u * 1024 * 1024)];
                    stream.Position = (long)offset + 8;
                    stream.ReadExactly(body);
                    if (cancellationToken.IsCancellationRequested) return null;
                    foreach (var (definition, check) in startupChecks)
                        if (check.Markers.All(marker => body.AsSpan().IndexOf(marker) >= 0))
                        {
                            startupRole ??= definition.Role;
                            break;
                        }
                }
                var match = AddonPattern().Match(Encoding.UTF8.GetString(prefix, 8, (int)Math.Min(bodyLength, (uint)prefix.Length - 8)));
                if (match.Success && !snapshot.AddonExcludes.Contains(match.Groups[1].Value)
                    && ResourceHash(match.Groups[1].Value) == name) declaredDependency = true;
            }
        }
        // 完成所有资源扫描后再确认启动标记命中，保证入口资源（如 MDL runtime）可覆盖共有 API 标记。
        if (startupRole is { } role) return role;
        // 旧版自带启动脚本的独立包不能仅凭同名资源被认定为依赖；可选 HUD Overlay 不在依赖名单中。
        return declaredDependency || registered && !ownsStartup ? BslModRole.Dependency : BslModRole.None;
    }

    private static bool DeclaresLoader(JsonElement value, LoaderRegistry.Snapshot snapshot)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return text is { } name && (snapshot.DependencyNames.Contains(name)
                || Guid.TryParse(name, out var id) && snapshot.DependencyGuids.Contains(id));
        }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(item => DeclaresLoader(item, snapshot));
        if (value.ValueKind != JsonValueKind.Object) return false;
        return value.EnumerateObject().Any(property =>
            (snapshot.DependencyKeys.Contains(property.Name)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var api) && api > 0)
            || DeclaresLoader(property.Value, snapshot));
    }

    /// <summary>BSL 的 seed-zero MurmurHash64A；资源名使用 ASCII。</summary>
    public static ulong ResourceHash(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        const ulong m = 0xc6a4a7935bd1e995;
        unchecked
        {
            var hash = (ulong)bytes.Length * m;
            var index = 0;
            for (; index + 8 <= bytes.Length; index += 8)
            {
                var k = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(index));
                k *= m; k ^= k >> 47; k *= m;
                hash ^= k; hash *= m;
            }
            ulong tail = 0;
            for (var i = index; i < bytes.Length; i++) tail |= (ulong)bytes[i] << ((i - index) * 8);
            if (index < bytes.Length) { hash ^= tail; hash *= m; }
            hash ^= hash >> 47; hash *= m; hash ^= hash >> 47;
            return hash;
        }
    }
}
