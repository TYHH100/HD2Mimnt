using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasPackageRequest(
    string ModelId,
    string Name,
    string Author,
    string Description,
    string? IconPath,
    byte[]? Icon,
    string? IconMime,
    IasCompiledModel Compiled,
    IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> Available,
    IasGameData Game,
    IReadOnlyList<string>? MaterialHints = null,
    IReadOnlyList<Hd2ClOptionGroup>? OptionGroups = null);

public sealed record IasPackagedModel(
    string Name,
    IReadOnlyDictionary<string, byte[]> Members,
    IasValue Manifest,
    IasValue Registration,
    string CatalogCodeSha256);

/// <summary>上游 <c>manual_backend.package_data</c> 的非内置成品形态。</summary>
public static class IasModelPackager
{
    private const string Patch = "9ba626afa44a3aa3.patch_1";
    private const string MaterialPatch = "9ba626afa44a3aa3.patch_2";
    private const string PackageArchive = "9ba626afa44a3aa3";
    private const ulong PackageId = 0x9ba626afa44a3aa3;
    private const int CatalogMaxBytes = 8_388_608;

    public static IasPackagedModel Build(IasPackageRequest request)
    {
        var owned = new Dictionary<(ulong Type, ulong Id), IasResource>(request.Compiled.Owned);
        // 大目录的 Lua 字面量只生成一次：重复调用会在 profile 归并阶段做两遍整树遍历。
        var catalogExpression = CatalogExpression(request.Compiled.Catalog);
        var registration = With(request.Compiled.Registration, "delivery", IasValue.Of("manual-adapted-v1"));
        if (request.Icon is not null)
        {
            var icon = request.Icon;
            // 上游预览模块是 catalog 的兄弟节点（.../v1/preview），宿主按该路径取图；
            // 写成 catalog/preview 时登记存在但游戏内面板取不到预览。
            var module = PreviewModule(Module(request.Compiled));
            var mime = request.IconMime ?? DetectMime(icon);
            var preview = IasValue.Obj(
                ("version", IasValue.Of(1L)), ("module", IasValue.Of(module)),
                ("encoding", IasValue.Of("base64")), ("mime", IasValue.Of(mime)),
                ("bytes", IasValue.Of(icon.Length)), ("sha256", IasValue.Of(IasHash.Sha256Hex(icon))));
            registration = With(registration, "preview", preview);
            var raw = Encoding.ASCII.GetBytes("-- HD2CL_PREVIEW_V1\nreturn [==[")
                .Concat(Encoding.ASCII.GetBytes(Convert.ToBase64String(icon)))
                .Concat("]==]\n"u8.ToArray()).ToArray();
            owned[(IasResourceTypes.Lua, IasHash.Murmur64(module))] = LuaResource(raw);
        }
        var required = new HashSet<ulong>(request.Compiled.VisibleMaterialIds);
        var pending = new Queue<ulong>(required.Order());
        while (pending.Count > 0)
        {
            var uid = pending.Dequeue();
            var resource = ResolveMaterial(request, uid);
            if (resource is null || resource.Toc.Length < 32) continue;
            var parent = BitConverter.ToUInt64(resource.Toc, 24);
            if (parent != 0 && required.Add(parent)) pending.Enqueue(parent);
        }
        var used = required.Order().Where(id => request.Available.ContainsKey((IasResourceTypes.Material, id))).ToList();
        var dependenciesUsed = required.Order().ToList();
        // 源模组自带的材质与它引用的贴图随成品一起打包；否则禁用源模组后游戏只剩默认材质。
        // 只读取当前可见材质及其父链，原生（游戏内）材质仍按资源编号引用。
        var bundledMaterials = new Dictionary<(ulong Type, ulong Id), IasResource>();
        foreach (var id in used)
        {
            var material = request.Available[(IasResourceTypes.Material, id)];
            bundledMaterials[(IasResourceTypes.Material, id)] = material;
            foreach (var textureId in TextureDependencies(material.Toc))
            {
                if (request.Available.TryGetValue((IasResourceTypes.Texture, textureId), out var texture))
                    bundledMaterials[(IasResourceTypes.Texture, textureId)] = texture;
            }
        }
        registration = With(registration, "materials",
            IasValue.Of(used.Select(id => IasValue.Of($"{id:x16}")).ToList()));
        var code = Encoding.ASCII.GetBytes("-- HD2CL_MODEL_V1 ")
            .Concat(IasLua.JsonBytes(registration))
            .Concat("\nreturn "u8.ToArray())
            .Concat(Encoding.UTF8.GetBytes(catalogExpression))
            .Concat("\n"u8.ToArray()).ToArray();
        IasBinary.Require(code.Length + 8 <= CatalogMaxBytes, "生成的模型目录超过接收容量。");
        owned[(IasResourceTypes.Lua, IasHash.Murmur64(Module(request.Compiled)))] = LuaResource(code);
        if (request.Compiled.Domains.Count > 0)
        {
            var encoded = IasNativeModelData.Encode(Module(request.Compiled), request.ModelId, request.Compiled.Domains);
            var spec = RegistrationNativeData(registration);
            IasBinary.Require(spec is not null && spec.Version == encoded.Spec.Version
                              && spec.Module == encoded.Spec.Module && spec.Sha256 == encoded.Spec.Sha256,
                "物理登记与原数据不一致。");
            owned[(IasResourceTypes.Lua, IasHash.Murmur64(encoded.Spec.Module))] = LuaResource(encoded.Raw);
        }
        var dependencies = owned.Keys.Concat(dependenciesUsed.Select(id => (IasResourceTypes.Material, id)))
            .Distinct().OrderBy(pair => pair.Item1).ThenBy(pair => pair.Item2).ToList();
        var packageHeader = ((byte[])request.Game.Resource(PackageArchive, IasResourceTypes.Package, PackageId).Toc.Clone())[..16];
        BitConverter.GetBytes((uint)dependencies.Count).CopyTo(packageHeader, 8);
        var packageToc = packageHeader.Concat(dependencies.SelectMany(pair =>
            BitConverter.GetBytes(pair.Item1).Concat(BitConverter.GetBytes(pair.Item2)))).ToArray();
        owned[(IasResourceTypes.Package, ulong.Parse(RegistrationString(registration, "package"), System.Globalization.NumberStyles.HexNumber))]
            = new IasResource(packageToc, [], []);
        IasBinary.Require(!owned.Keys.Any(pair => pair.Type is IasResourceTypes.Material or IasResourceTypes.Texture),
            "模型补丁混入材质或贴图。");
        var manifest = IasValue.Obj(
            ("Name", IasValue.Of(request.Name)),
            ("Guid", IasValue.Of(Uuid5(NamespaceUrl, "HD2CharacterLibrary/" + request.ModelId + "/adapted-model"))),
            ("Author", IasValue.Of(request.Author)),
            ("Description", IasValue.Of(request.Description)));
        if (request.IconPath is not null && request.Icon is not null)
            manifest = With(manifest, "IconPath", IasValue.Of(request.IconPath));
        if (request.OptionGroups is { Count: > 0 })
        {
            manifest = With(manifest, "Version", IasValue.Of(1L));
            manifest = With(manifest, "Options", OptionManifest(request.OptionGroups));
        }
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (request.IconPath is not null && request.Icon is not null) members[request.IconPath] = request.Icon;
        members["manifest.json"] = Encoding.UTF8.GetBytes(IasLua.WriteJson(manifest, ensureAscii: false, sortKeys: false, indent: 2));
        var buffers = IasArchive.Write(owned);
        IasArchive.VerifyReadback(buffers.Toc, buffers.Gpu, buffers.Stream, owned);
        members[Patch] = buffers.Toc;
        members[Patch + ".gpu_resources"] = buffers.Gpu;
        members[Patch + ".stream"] = buffers.Stream;
        if (bundledMaterials.Count > 0)
        {
            var materialBuffers = IasArchive.Write(bundledMaterials);
            IasArchive.VerifyReadback(materialBuffers.Toc, materialBuffers.Gpu, materialBuffers.Stream, bundledMaterials);
            members[MaterialPatch] = materialBuffers.Toc;
            members[MaterialPatch + ".gpu_resources"] = materialBuffers.Gpu;
            members[MaterialPatch + ".stream"] = materialBuffers.Stream;
        }
        return new IasPackagedModel(request.Name, members, manifest, registration,
            IasHash.Sha256Hex(Encoding.UTF8.GetBytes(catalogExpression)));
    }

    /// <summary>
    /// 把成品成员直接写入目录。转换结果最终都会导入管理器，先压成 7z 再解压只会多一轮 I/O 与 CPU；
    /// 需要单独分发压缩包时再另加导出选项。
    /// </summary>
    public static async Task WriteMembersAsync(string directory, IReadOnlyDictionary<string, byte[]> members,
        CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        foreach (var (name, raw) in members)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, raw, token);
        }
    }

    public static string CatalogExpression(IasValue catalog)
    {
        var literal = IasLua.Literal(catalog);
        if (Encoding.UTF8.GetByteCount(literal) <= 4_190_208) return literal;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var literals = new Dictionary<IasValue.Object, string>(ReferenceEqualityComparer.Instance);
        string LiteralOf(IasValue.Object obj)
        {
            if (literals.TryGetValue(obj, out var text)) return text;
            text = IasLua.Literal(obj);
            literals[obj] = text;
            return text;
        }
        CollectProfiles(catalog, counts, LiteralOf);
        var shared = counts.Where(pair => pair.Value > 1).Select(pair => pair.Key)
            .OrderBy(text => text, IasCodePointComparer.Instance)
            .Select((text, index) => (text, index: index + 1))
            .ToDictionary(pair => pair.text, pair => pair.index, StringComparer.Ordinal);
        string Encode(IasValue value) => value switch
        {
            IasValue.Object obj when IsProfileObject(obj) && shared.TryGetValue(LiteralOf(obj), out var index)
                => "_profiles[" + index + "]",
            IasValue.Object obj => "{" + string.Join(",", obj.Entries
                .OrderBy(entry => entry.Key, IasCodePointComparer.Instance)
                .Select(entry => "[" + IasLua.QuoteJson(entry.Key, ensureAscii: true) + "]=" + Encode(entry.Value))) + "}",
            IasValue.Array array => "{" + string.Join(",", array.Items.Select(Encode)) + "}",
            _ => IasLua.Literal(value)
        };
        return "(function() local _profiles={" + string.Join(",", shared.Keys.OrderBy(text => shared[text])) + "}; return "
               + Encode(catalog) + " end)()";
    }

    private static void CollectProfiles(IasValue value, Dictionary<string, int> counts,
        Func<IasValue.Object, string> literalOf)
    {
        switch (value)
        {
            case IasValue.Object obj:
                if (IsProfileObject(obj))
                {
                    var text = literalOf(obj);
                    counts[text] = counts.GetValueOrDefault(text) + 1;
                }
                foreach (var entry in obj.Entries) CollectProfiles(entry.Value, counts, literalOf);
                break;
            case IasValue.Array array:
                foreach (var item in array.Items) CollectProfiles(item, counts, literalOf);
                break;
        }
    }

    private static bool IsProfileObject(IasValue.Object obj)
        => obj.Entries.FirstOrDefault(entry => entry.Key == "nodes").Value is IasValue.Array { Items.Count: > 0 } nodes
           && nodes.Items.All(item => item is IasValue.Object);

    private static IasResource? ResolveMaterial(IasPackageRequest request, ulong uid)
    {
        if (request.Available.TryGetValue((IasResourceTypes.Material, uid), out var resource)) return resource;
        var archives = new List<string>();
        if (request.MaterialHints is not null) archives.AddRange(request.MaterialHints);
        archives.Add("18235e0c9ec0e636");
        archives.Add(PackageArchive);
        foreach (var archive in archives.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return request.Game.Resource(archive, IasResourceTypes.Material, uid);
            }
            catch (KeyNotFoundException)
            {
            }
            catch (FileNotFoundException)
            {
            }
        }
        return null;
    }

    /// <summary>Material TOC：偏移 64 是贴图数量，136 + 4*count 起是数量个 uint64 贴图编号。</summary>
    private static IEnumerable<ulong> TextureDependencies(byte[] toc)
    {
        if (toc.Length < 68) yield break;
        var count = BitConverter.ToUInt32(toc, 64);
        IasBinary.Require(count <= 4096, "材质贴图数量超出预期。");
        var offset = 136L + 4L * count;
        IasBinary.Require(offset + 8L * count <= toc.Length, "材质贴图表越界。");
        for (var index = 0; index < count; index++)
        {
            var id = BitConverter.ToUInt64(toc, (int)(offset + 8L * index));
            if (id != 0) yield return id;
        }
    }

    private static string Module(IasCompiledModel compiled)
        => RegistrationString(compiled.Registration, "module");

    /// <summary>上游把预览登记为 catalog 的兄弟节点（.../v1/preview），宿主按此路径取缩略图。</summary>
    internal static string PreviewModule(string catalogModule)
    {
        var slash = catalogModule.LastIndexOf('/');
        return slash < 0 ? "preview" : catalogModule[..(slash + 1)] + "preview";
    }

    private static string RegistrationString(IasValue registration, string key)
        => (string)((IasValue.Text)((IasValue.Object)registration).Entries
            .First(entry => entry.Key == key).Value).Value;

    private static IasNativeDataSpec? RegistrationNativeData(IasValue registration)
    {
        if (((IasValue.Object)registration).Entries.FirstOrDefault(entry => entry.Key == "native_data").Value is not IasValue.Object spec)
            return null;
        string Read(string key) => (string)((IasValue.Text)spec.Entries.First(entry => entry.Key == key).Value).Value;
        long version = ((IasValue.Integer)spec.Entries.First(entry => entry.Key == "version").Value).Value;
        return new IasNativeDataSpec((int)version, Read("module"), Read("sha256"));
    }

    /// <summary>
    /// 成品的 V1 选项树：首个开关映射根目录，保证模型与自带材质补丁始终参与部署
    /// （有 Include 映射时只展开列出的目录，根目录不列就会被跳过）；
    /// 其后每个互斥配色组一组子选项，Include 指向复制进成品的 Material 目录。
    /// </summary>
    internal static IasValue OptionManifest(IReadOnlyList<Hd2ClOptionGroup> groups)
    {
        var options = new List<IasValue>
        {
            IasValue.Obj(
                ("Name", IasValue.Of("模型")),
                ("Description", IasValue.Of("Model")),
                ("Image", IasValue.Nil.Instance),
                ("DefaultEnabled", IasValue.Of(true)),
                ("Include", IasValue.Of(new List<IasValue> { IasValue.Of(".") })))
        };
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group.Choices.Count == 1)
            {
                // 只有一个分支的组用普通开关，避免出现只有一项的下拉框。
                options.Add(IasValue.Obj(
                    ("Name", IasValue.Of(group.Name)),
                    ("Description", IasValue.Of(group.Description ?? string.Empty)),
                    ("Image", group.Image is null ? IasValue.Nil.Instance : IasValue.Of(group.Image)),
                    ("DefaultEnabled", IasValue.Of(group.Enabled)),
                    ("Include", IasValue.Of(new List<IasValue>
                    {
                        IasValue.Of($"Material/{groupIndex + 1}/1")
                    }))));
                continue;
            }
            var choices = new List<IasValue>();
            for (var choiceIndex = 0; choiceIndex < group.Choices.Count; choiceIndex++)
            {
                var choice = group.Choices[choiceIndex];
                choices.Add(IasValue.Obj(
                    ("Name", IasValue.Of(choice.Name)),
                    ("Description", IasValue.Of(string.Empty)),
                    ("Image", choice.Image is null ? IasValue.Nil.Instance : IasValue.Of(choice.Image)),
                    ("Include", IasValue.Of(new List<IasValue>
                    {
                        IasValue.Of($"Material/{groupIndex + 1}/{choiceIndex + 1}")
                    }))));
            }
            options.Add(IasValue.Obj(
                ("Name", IasValue.Of(group.Name)),
                ("Description", IasValue.Of(group.Description ?? string.Empty)),
                ("Image", group.Image is null ? IasValue.Nil.Instance : IasValue.Of(group.Image)),
                ("DefaultEnabled", IasValue.Of(group.Enabled)),
                ("SubOptions", IasValue.Of(choices))));
        }
        return IasValue.Of(options);
    }

    private static IasValue With(IasValue value, string key, IasValue replacement)
    {
        var entries = ((IasValue.Object)value).Entries.ToList();
        var index = entries.FindIndex(entry => entry.Key == key);
        if (index >= 0) entries[index] = new KeyValuePair<string, IasValue>(key, replacement);
        else entries.Add(new KeyValuePair<string, IasValue>(key, replacement));
        return new IasValue.Object(entries);
    }

    private static IasResource LuaResource(byte[] raw)
        => new(BitConverter.GetBytes((uint)raw.Length).Concat(BitConverter.GetBytes(2u)).Concat(raw).ToArray(), [], []);

    private static string DetectMime(ReadOnlySpan<byte> icon)
        => icon.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) ? "image/png"
            : icon.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? "image/jpeg"
            : icon.StartsWith("RIFF"u8) && icon[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
            : "application/octet-stream";

    private static readonly Guid NamespaceUrl = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    private static string Uuid5(Guid space, string name)
    {
        var hash = SHA1.HashData(space.ToByteArray(bigEndian: true).Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString();
    }
}
