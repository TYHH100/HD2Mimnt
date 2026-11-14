using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;

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
    IReadOnlyList<string>? MaterialHints = null);

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
    private const string PackageArchive = "9ba626afa44a3aa3";
    private const ulong PackageId = 0x9ba626afa44a3aa3;
    private const int CatalogMaxBytes = 8_388_608;

    public static IasPackagedModel Build(IasPackageRequest request)
    {
        var owned = new Dictionary<(ulong Type, ulong Id), IasResource>(request.Compiled.Owned);
        var registration = With(request.Compiled.Registration, "delivery", IasValue.Of("manual-adapted-v1"));
        if (request.Icon is not null)
        {
            var icon = request.Icon;
            var module = Module(request.Compiled) + "/preview";
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
        registration = With(registration, "materials",
            IasValue.Of(used.Select(id => IasValue.Of($"{id:x16}")).ToList()));
        var code = Encoding.ASCII.GetBytes("-- HD2CL_MODEL_V1 ")
            .Concat(IasLua.JsonBytes(registration))
            .Concat("\nreturn "u8.ToArray())
            .Concat(Encoding.UTF8.GetBytes(CatalogExpression(request.Compiled.Catalog)))
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
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (request.IconPath is not null && request.Icon is not null) members[request.IconPath] = request.Icon;
        members["manifest.json"] = Encoding.UTF8.GetBytes(IasLua.WriteJson(manifest, ensureAscii: false, sortKeys: false, indent: 2));
        var buffers = IasArchive.Write(owned);
        var parsed = IasArchive.ReadEntries(buffers.Toc, buffers.Gpu, buffers.Stream);
        IasBinary.Require(parsed.Count == owned.Count, "模型补丁回读身份不一致。");
        foreach (var (key, value) in owned)
        {
            var loaded = parsed[key];
            IasBinary.Require(loaded.Toc.SequenceEqual(value.Toc) && loaded.Gpu.SequenceEqual(value.Gpu)
                              && loaded.Stream.SequenceEqual(value.Stream), "模型补丁回读载荷不一致。");
        }
        members[Patch] = buffers.Toc;
        members[Patch + ".gpu_resources"] = buffers.Gpu;
        members[Patch + ".stream"] = buffers.Stream;
        return new IasPackagedModel(request.Name, members, manifest, registration,
            IasHash.Sha256Hex(Encoding.UTF8.GetBytes(CatalogExpression(request.Compiled.Catalog))));
    }

    /// <summary>用随程序分发的 7za 写出与上游一致的 LZMA2 固实包，并回读校验成员。</summary>
    public static async Task WriteArchiveAsync(string archivePath, IReadOnlyDictionary<string, byte[]> members,
        CancellationToken token)
    {
        var work = Path.Combine(Path.GetTempPath(), "hd2cl-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (var (name, raw) in members)
            {
                var target = Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, raw, token);
            }
            var executable = Path.Combine(AppContext.BaseDirectory, "Tools", "7zip", "7za.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Creator.SevenZipMissing", executable);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "a", "-t7z", "-m0=LZMA2", "-mx=7", "-md=128m", "-mfb=273",
                         "-ms=on", "-mmt=1", "-scsUTF-8", "-sccUTF-8", "-bso0", "-bsp0", "-bse2", "-y" })
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add(archivePath);
            foreach (var name in members.Keys.Order(StringComparer.Ordinal)) start.ArgumentList.Add(name);
            using var process = Process.Start(start) ?? throw new IOException("Could not start 7za.");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, errors);
            if (process.ExitCode != 0) throw new IOException($"7-Zip ({process.ExitCode}): {(await errors).Trim()}");
            token.ThrowIfCancellationRequested();
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var extracted = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
            {
                using var stream = entry.OpenEntryStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, token);
                extracted[entry.Key.Replace('\\', '/')] = buffer.ToArray();
            }
            IasBinary.Require(extracted.Count == members.Count
                              && members.All(pair => extracted.TryGetValue(pair.Key, out var raw) && raw.SequenceEqual(pair.Value)),
                "7z 解压回读文件不一致。");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    public static string CatalogExpression(IasValue catalog)
    {
        var literal = IasLua.Literal(catalog);
        if (Encoding.UTF8.GetByteCount(literal) <= 4_190_208) return literal;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        CollectProfiles(catalog, counts);
        var shared = counts.Where(pair => pair.Value > 1).Select(pair => pair.Key)
            .OrderBy(text => text, IasCodePointComparer.Instance)
            .Select((text, index) => (text, index: index + 1))
            .ToDictionary(pair => pair.text, pair => pair.index, StringComparer.Ordinal);
        string Encode(IasValue value) => value switch
        {
            IasValue.Object obj when IsProfileObject(obj) && shared.TryGetValue(IasLua.Literal(obj), out var index)
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

    private static void CollectProfiles(IasValue value, Dictionary<string, int> counts)
    {
        switch (value)
        {
            case IasValue.Object obj:
                if (IsProfileObject(obj))
                {
                    var text = IasLua.Literal(obj);
                    counts[text] = counts.GetValueOrDefault(text) + 1;
                }
                foreach (var entry in obj.Entries) CollectProfiles(entry.Value, counts);
                break;
            case IasValue.Array array:
                foreach (var item in array.Items) CollectProfiles(item, counts);
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

    private static string Module(IasCompiledModel compiled)
        => RegistrationString(compiled.Registration, "module");

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
