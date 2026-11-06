using HD2Mimnt.ModelConversion.Gmod.Parts;
using HD2Mimnt.ModelConversion.Hd2;
using HD2Mimnt.ModelConversion.Ias;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2Mimnt.ModelConversion.Gmod.Packaging;

/// <summary>补丁中一个 Unit 资源的产出记录。</summary>
public sealed record GmodStingrayUnit(ulong UnitId, string PartId, string OptionId, string OptionLabel,
    int MeshCount, int VertexCount, int TriangleCount, int MainBytes, int GpuBytes, bool Placeholder)
{
    /// <summary>占位 Unit 的用途：清空目标护甲里未被接管的槽位。</summary>
    public bool Cleanup { get; init; }
}

public sealed record GmodStingrayMaterialResource(string SourcePath, ulong MaterialId, uint Slot, string Family,
    int TextureCount, int Bytes);

public sealed record GmodStingrayTextureResource(string SourcePath, string Role, ulong TextureId, int Width,
    int Height, int MipCount, string Format);

public sealed record GmodStingraySummary(int UnitCount, int MaterialCount, int TextureCount, int MeshCount,
    int VertexCount, int TriangleCount, int PlaceholderUnitCount, int RelocatedPayloads, long PatchBytes,
    long GpuBytes, long StreamBytes, int MaxPaletteSize, string PatchFile, string Guid)
{
    /// <summary>为清空目标护甲其余槽位而写的占位 Unit 数量。</summary>
    public int CleanupUnitCount { get; init; }
}

/// <summary>
/// 一个可直接导入模组库并部署的 data 模组包：<c>package/manifest.json</c> +
/// <c>package/data/9ba626afa44a3aa3.patch_0{,.gpu_resources,.stream}</c>。
/// </summary>
public sealed record GmodRunnablePackage(string Directory, IReadOnlyList<GmodOutputFile> Files,
    IReadOnlyList<GmodStingrayUnit> Units, IReadOnlyList<GmodStingrayMaterialResource> Materials,
    IReadOnlyList<GmodStingrayTextureResource> Textures, GmodStingraySummary Summary,
    IReadOnlyList<GmodConversionIssue> Issues);

/// <summary>
/// GMOD 转换结果 → 游戏原生 Stingray 资源 → 可运行 data 模组包。
///
/// 每个已发布 Unit（IAS 路由里的 Published Unit ID）产出一份真正的 <c>.unit</c>，加上被它引用的
/// <c>.material</c> 与贴图，全部写进 <c>9ba626afa44a3aa3.patch_0</c> 三件套；同时给出标准 V1
/// <c>manifest.json</c> 与 <c>hd2mimnt.json</c> 扩展，文件夹本身即可导入模组库。
///
/// 材质族与通道配方（basic / alphaclip）来自实测模板，贴图缺失通道用中性值补齐，
/// 所有近似处理都会以 Info 级提示写进报告，不假装还原了 Source 着色器的运行时行为。
/// </summary>
public static class GmodStingrayPackager
{
    /// <summary>包内模组根目录名（与部位目录同级，避免覆盖部位自身的审计清单）。</summary>
    public const string PackageDirectory = "package";

    /// <summary>超过这个体积就提示用户收窄开关：每个选项都要独占一份 Unit 载荷。</summary>
    public const long LargePackageBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>标准 V1 清单与 hd2mimnt.json 用 PascalCase 字段名，不能套用报告用的 snake_case 策略。</summary>
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static GmodRunnablePackage Build(GmodConversionPart part, IReadOnlyList<GmodMaterial> materials,
        IReadOnlyList<GmodTexture> textures, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(textures);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new List<GmodConversionIssue>();
        var allocator = new Hd2ResourceIdAllocator(part.ArchiveId);

        var meshIndex = new Dictionary<string, GmodMesh>(StringComparer.Ordinal);
        foreach (var mesh in part.Geometry.Meshes) meshIndex.TryAdd(mesh.Id, mesh);
        var materialIndex = new Dictionary<string, GmodMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials) materialIndex.TryAdd(Normalize(material.Path), material);
        var textureIndex = new Dictionary<string, GmodTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures) textureIndex.TryAdd(Normalize(texture.Path), texture);

        // 单位路由先占位：Patch 以 (FileID, TypeID) 为身份，Unit ID 由 IAS 路由决定，不能改。
        var plannedUnits = new List<(string PartId, string OptionId, string Label, string[] MeshIds, ulong UnitId, bool Cleanup)>();
        foreach (var freePart in part.Differences.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var option in freePart.Options)
            {
                var geometry = part.Geometry.Options.FirstOrDefault(item => item.OptionId == option.Id);
                var ids = geometry?.MeshIds.Where(meshIndex.ContainsKey).ToArray() ?? [];
                if (geometry is null)
                    issues.Add(new GmodConversionIssue("OptionGeometryMissing", option.Id,
                        "The switch option has no geometry in the converted bundle; its Unit is written as an empty placeholder.",
                        GmodIssueSeverity.Warning));
                foreach (var route in option.Routes)
                {
                    if (!allocator.Reserve(route.PublishedUnitId))
                    {
                        issues.Add(new GmodConversionIssue("DuplicateUnitRoute", option.Id,
                            $"Published Unit 0x{route.PublishedUnitId:x16} is claimed by more than one option; only the first is written.",
                            GmodIssueSeverity.Error));
                        continue;
                    }
                    plannedUnits.Add((freePart.Id, option.Id, option.DisplayName, ids, route.PublishedUnitId, false));
                }
            }
        }

        // 目标护甲里没有被接管的 Unit：写零面积占位 Unit，让原甲片彻底消失而不是和模型叠在一起。
        foreach (var route in part.CleanupRoutes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!allocator.Reserve(route.PublishedUnitId))
            {
                issues.Add(new GmodConversionIssue("DuplicateUnitRoute", "cleanup",
                    $"Cleanup Unit 0x{route.PublishedUnitId:x16} collides with another resource and was skipped.",
                    GmodIssueSeverity.Error));
                continue;
            }
            plannedUnits.Add(("cleanup", route.NativeUnitId.ToString("x16", CultureInfo.InvariantCulture),
                "Cleared armour slot", [], route.PublishedUnitId, true));
        }
        if (plannedUnits.Count == 0)
            throw new Hd2FormatException("The converted part has no published Unit to write into a patch.");

        // 1. 贴图与材质资源（按第一次被引用时的顺序分配 ID，保证同样的输入得到同样的输出）。
        var textureResources = new Dictionary<string, TextureResource>(StringComparer.Ordinal);
        var materialResources = new Dictionary<string, MaterialResource>(StringComparer.OrdinalIgnoreCase);
        var usedSlots = new Dictionary<uint, string>();
        var reportedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2. 每个已发布 Unit 一份 .unit；网格完全相同的选项共用同一份载荷。
        var transformCache = part.Geometry.Bones
            .Select((bone, index) => new Hd2UnitTransform(bone.Name,
                bone.Parent >= 0 && bone.Parent < index ? bone.Parent : -1, GmodBonePose.Local(bone)))
            .ToArray();
        var units = new List<GmodStingrayUnit>(plannedUnits.Count);
        var unitPayloads = new List<(ulong UnitId, UnitPayload Payload)>(plannedUnits.Count);
        var payloadBySignature = new Dictionary<string, UnitPayload>(StringComparer.Ordinal);
        var relocated = 0;
        var maxPalette = 0;

        foreach (var planned in plannedUnits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = (planned.Cleanup ? "cleanup|" : "") + string.Join(",", planned.MeshIds) + "|" + string.Join(",",
                planned.MeshIds.Select(id => Normalize(meshIndex[id].MaterialPath)));
            if (!payloadBySignature.TryGetValue(signature, out var payload))
            {
                var (definition, meshCount, vertexCount, triangleCount) =
                    BuildUnitDefinition(planned.MeshIds, transformCache, meshIndex, issues.Add, planned.Cleanup);
                var built = Hd2UnitBuilder.Build(definition, cancellationToken);
                maxPalette = Math.Max(maxPalette, built.Summary.MaxPaletteSize);
                payload = new UnitPayload(built.Main, built.Gpu, meshCount, vertexCount, triangleCount);
                payloadBySignature[signature] = payload;
            }
            else
            {
                relocated++;
            }

            units.Add(new GmodStingrayUnit(planned.UnitId, planned.PartId, planned.OptionId, planned.Label,
                payload.MeshCount, payload.VertexCount, payload.TriangleCount, payload.Main.Length,
                payload.Gpu.Length, planned.MeshIds.Length == 0)
            {
                Cleanup = planned.Cleanup
            });
            unitPayloads.Add((planned.UnitId, payload));
        }

        // 3. 打补丁：贴图 → 材质 → Unit（叶子在前，便于阅读 TOC 与排查）。
        var resources = new List<StingrayResource>(
            textureResources.Count + materialResources.Count + unitPayloads.Count);
        foreach (var texture in textureResources.Values)
            resources.Add(new StingrayResource(texture.Id, StingrayTypeIds.Texture, texture.Main, texture.Gpu,
                ReadOnlyMemory<byte>.Empty));
        foreach (var material in materialResources.Values)
            resources.Add(new StingrayResource(material.Id, StingrayTypeIds.Material, material.Bytes));
        foreach (var (unitId, payload) in unitPayloads)
            resources.Add(new StingrayResource(unitId, StingrayTypeIds.Unit, payload.Main, payload.Gpu,
                ReadOnlyMemory<byte>.Empty));

        var bundle = StingrayPatchWriter.Build(resources, cancellationToken);

        // 4. 模组包：标准 V1 清单 + HD2Mimnt 扩展 + 资源索引。
        var projectId = part.Differences.ManifestId;
        var guid = DerivePackageGuid(projectId, part.ArchiveId);
        var prefix = PackageDirectory + "/";
        var files = new List<GmodOutputFile>();
        void Add(string path, string kind, byte[] data) =>
            files.Add(new GmodOutputFile(path, kind, data,
                System.Convert.ToHexStringLower(SHA256.HashData(data))));

        Add(prefix + "data/" + StingrayPatchWriter.DefaultPatchName, "patch", bundle.Patch);
        // 三个伴随文件必须同时存在，哪怕是 0 字节：AQSDK 的 PACK_SUFFIXES 就是
        // ("", ".gpu_resources", ".stream")，read_archive_payloads 会无条件读这三个文件；
        // 真实环境里 patch_1/patch_2 也各有一个 0 字节的 .stream / .gpu_resources，
        // 只有主文件而缺伴随文件是磁盘上看不到的偏差，排查起来最费时间。
        Add(prefix + "data/" + StingrayPatchWriter.DefaultPatchName + ".gpu_resources", "patch-gpu", bundle.Gpu);
        Add(prefix + "data/" + StingrayPatchWriter.DefaultPatchName + ".stream", "patch-stream", bundle.Stream);

        Add(prefix + "manifest.json", "mod-manifest", JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Guid = guid,
            Name = PackageName(projectId, part.Kind),
            Description = "Generated by HD2Mimnt from a GMOD model. Native Stingray unit, material and texture " +
                "resources, ready to deploy into the game data directory.",
            Options = new[] { new { Name = "data", Include = new[] { "data" } } }
        }, ManifestJson));

        Add(prefix + "hd2mimnt.json", "mod-metadata", JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            ManifestGuid = guid,
            Extensions = new
            {
                Author = "HD2Mimnt",
                ModVersion = "1.0.0",
                Category = "Armor",
                Tags = new[] { "gmod", "ias", part.Kind.ToString().ToLowerInvariant() },
                GeneratedManifest = true
            }
        }, ManifestJson));

        var summary = new GmodStingraySummary(units.Count, materialResources.Count, textureResources.Count,
            units.Sum(unit => unit.MeshCount), units.Sum(unit => unit.VertexCount),
            units.Sum(unit => unit.TriangleCount), units.Count(unit => unit.Placeholder), relocated,
            bundle.Patch.LongLength, bundle.Gpu.LongLength, bundle.Stream.LongLength,
            maxPalette, StingrayPatchWriter.DefaultPatchName, guid)
        {
            CleanupUnitCount = units.Count(unit => unit.Cleanup)
        };

        Add(prefix + "hd2mimnt-gmod.json", "package-index", JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "HD2GmodPackage1",
            project_id = projectId,
            part = part.Kind.ToString().ToLowerInvariant(),
            domain = IasFreePartsCodec.DomainName(part.Domain),
            archive = "0x" + part.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
            target_armor = part.Target is null ? null : new
            {
                code = part.Target.Code,
                name = part.Target.Name,
                display_name = part.Target.DisplayName,
                carrier_unit = "0x" + part.Target.CarrierUnitId.ToString("x16", CultureInfo.InvariantCulture),
                cleaned_unit_count = part.CleanupRoutes.Count
            },
            unit_version = Hd2UnitBuilder.UnitVersion,
            vertex_stride = Hd2UnitBuilder.VertexStride,
            patch = StingrayPatchWriter.DefaultPatchName,
            gpu_resources = StingrayPatchWriter.DefaultPatchName + ".gpu_resources",
            stream = StingrayPatchWriter.DefaultPatchName + ".stream",
            summary = new
            {
                unit_count = summary.UnitCount,
                cleanup_unit_count = summary.CleanupUnitCount,
                material_count = summary.MaterialCount,
                texture_count = summary.TextureCount,
                patch_bytes = summary.PatchBytes,
                gpu_bytes = summary.GpuBytes
            },
            units = units.Select(unit => new
            {
                unit_id = "0x" + unit.UnitId.ToString("x16", CultureInfo.InvariantCulture),
                part = unit.PartId,
                option = unit.OptionId,
                label = unit.OptionLabel,
                mesh_count = unit.MeshCount,
                vertex_count = unit.VertexCount,
                triangle_count = unit.TriangleCount,
                placeholder = unit.Placeholder,
                cleanup = unit.Cleanup
            }),
            materials = materialResources.Values.Select(material => new
            {
                source = material.SourcePath,
                material_id = "0x" + material.Id.ToString("x16", CultureInfo.InvariantCulture),
                slot = "0x" + material.Slot.ToString("x8", CultureInfo.InvariantCulture),
                family = material.Family == Hd2MaterialFamily.AlphaClip ? "alphaclip" : "basic",
                texture_count = material.TextureCount
            }),
            textures = textureResources.Values.Select(texture => new
            {
                source = texture.SourcePath,
                role = texture.Role,
                texture_id = "0x" + texture.Id.ToString("x16", CultureInfo.InvariantCulture),
                width = texture.Width,
                height = texture.Height,
                mip_count = texture.MipCount,
                format = texture.Format
            }),
            entries = bundle.Entries.Select(entry => new
            {
                file_id = "0x" + entry.FileId.ToString("x16", CultureInfo.InvariantCulture),
                type_id = "0x" + entry.TypeId.ToString("x16", CultureInfo.InvariantCulture),
                index = entry.Index,
                main_offset = entry.MainOffset,
                main_size = entry.MainSize,
                gpu_offset = entry.GpuOffset,
                gpu_size = entry.GpuSize
            })
        }, Json));

        // 每个开关选项都是一份独立的 Unit：选项多时补丁会按选项数成倍增长，必须给出可执行的建议。
        var totalBytes = summary.PatchBytes + summary.GpuBytes;
        if (totalBytes > LargePackageBytes)
            issues.Add(new GmodConversionIssue("PackageSizeLarge", part.Differences.ManifestId,
                $"The runnable package is {totalBytes / (1024 * 1024)} MiB because every switch option gets its own Unit " +
                $"({summary.UnitCount} units). Narrow the published switches (--switches / Body switches) to reduce it.",
                GmodIssueSeverity.Warning));

        issues.Add(new GmodConversionIssue("TextureChannelApproximation", part.Differences.ManifestId,
            "HD2 armour materials pack metal/roughness/AO into shared maps. Missing Source channels are filled with " +
            "neutral values (non-metal, rough, AO=1); Source shader proxies and conditions are not evaluated.",
            GmodIssueSeverity.Info));
        issues.Add(new GmodConversionIssue("RuntimeNotVerified", part.Differences.ManifestId,
            "The generated patch is structurally validated (TOC, Unit, Material and Texture self-checks) but has not " +
            "been verified inside the running game; the armour's bone mapping still comes from the IAS rig.",
            GmodIssueSeverity.Info));

        return new GmodRunnablePackage(PackageDirectory, files.AsReadOnly(), units.AsReadOnly(),
            materialResources.Values.Select(material => material.Report).ToArray(),
            textureResources.Values.Select(texture => texture.Report).ToArray(),
            summary, issues.AsReadOnly());

        // ---- 局部构建函数 ----------------------------------------------------

        (Hd2UnitDefinition Definition, int MeshCount, int VertexCount, int TriangleCount) BuildUnitDefinition(
            string[] meshIds, Hd2UnitTransform[] transforms, IReadOnlyDictionary<string, GmodMesh> meshes,
            Action<GmodConversionIssue> report, bool cleanup)
        {
            var sourceMeshes = meshIds.Select(id => meshes[id]).ToArray();
            if (sourceMeshes.Length == 0)
            {
                sourceMeshes = [Placeholder(meshes.Values.FirstOrDefault(), report, cleanup)];
            }

            var unitMaterials = new List<Hd2UnitMaterial>();
            var unitMaterialSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unitMeshes = new List<Hd2UnitMesh>(sourceMeshes.Length);
            var meshIdsUsed = new HashSet<uint>();

            foreach (var mesh in sourceMeshes)
            {
                var path = Normalize(string.IsNullOrWhiteSpace(mesh.MaterialPath) ? "(missing)" : mesh.MaterialPath);
                if (!unitMaterialSlots.TryGetValue(path, out var slotIndex))
                {
                    var resource = MaterialFor(path);
                    slotIndex = unitMaterials.Count;
                    unitMaterialSlots[path] = slotIndex;
                    unitMaterials.Add(new Hd2UnitMaterial(resource.Slot, resource.Id));
                }
                unitMeshes.Add(new Hd2UnitMesh(NextMeshId(mesh.Id, meshIdsUsed), slotIndex,
                    ConvertVertices(mesh.Vertices), mesh.Indices));
            }

            return (new Hd2UnitDefinition(transforms, unitMaterials, unitMeshes), unitMeshes.Count,
                unitMeshes.Sum(mesh => mesh.Vertices.Count), unitMeshes.Sum(mesh => mesh.Indices.Count / 3));
        }

        uint NextMeshId(string meshId, HashSet<uint> used)
        {
            var key = part.Kind + "|" + meshId;
            var hash = StingrayHash.Murmur32(key);
            for (var salt = 0; hash == 0 || !used.Add(hash); salt++)
                hash = StingrayHash.Murmur32(key + "#" + salt.ToString(CultureInfo.InvariantCulture));
            return hash;
        }

        GmodMesh Placeholder(GmodMesh? template, Action<GmodConversionIssue> report, bool cleanup)
        {
            report(cleanup
                ? new GmodConversionIssue("ArmourSlotCleared", part.Differences.ManifestId,
                    "Target-armour Units that this model does not take over are routed to a zero-area placeholder so the " +
                    "original plates stop rendering. All of them share one Unit payload.",
                    GmodIssueSeverity.Info)
                : new GmodConversionIssue("EmptySwitchOption", part.Differences.ManifestId,
                    "A switch option selects no mesh (for example a hidden bodygroup alternative). " +
                    "It is written as a zero-area triangle so the IAS route still resolves to a valid Unit.",
                    GmodIssueSeverity.Info));
            var origin = new Vector3(0f, 0f, 0f);
            var vertices = new[]
            {
                Vertex(origin), Vertex(origin), Vertex(origin)
            };
            return new GmodMesh("(empty)", -1, -1, -1, -1, -1,
                template?.MaterialPath ?? "(missing)", template?.ModelName ?? "empty", vertices, [0, 1, 2]);

            static GmodVertex Vertex(Vector3 position) => new(position, new Vector3(0f, 0f, 1f),
                Vector2.Zero, Vector4.Zero, [1f], [0]);
        }

        IReadOnlyList<Hd2UnitVertex> ConvertVertices(GmodVertex[] vertices)
        {
            var converted = new Hd2UnitVertex[vertices.Length];
            Span<float> weights = stackalloc float[4];
            Span<int> bones = stackalloc int[4];
            for (var i = 0; i < vertices.Length; i++)
            {
                var vertex = vertices[i];
                for (var lane = 0; lane < 4; lane++)
                {
                    weights[lane] = lane < vertex.Weights.Length ? vertex.Weights[lane] : 0f;
                    bones[lane] = lane < vertex.Bones.Length ? vertex.Bones[lane] : 0;
                }
                converted[i] = new Hd2UnitVertex(vertex.Position, vertex.Normal, vertex.TexCoord,
                    weights[0], weights[1], weights[2], weights[3],
                    bones[0], bones[1], bones[2], bones[3]);
            }
            return converted;
        }

        MaterialResource MaterialFor(string path)
        {
            if (materialResources.TryGetValue(path, out var cached)) return cached;
            materialIndex.TryGetValue(path, out var material);
            if (material is null)
                issues.Add(new GmodConversionIssue("MaterialMissing", path,
                    "The mesh references a material that is not in the addon; a neutral armour material is generated.",
                    GmodIssueSeverity.Warning));

            var family = ResolveFamily(material);
            var inputs = family == Hd2MaterialFamily.AlphaClip ? AlphaClipInputs(material) : BasicInputs(material);
            var built = Hd2MaterialWriter.Build(family, inputs);
            var slot = SlotFor(path);
            var resource = new MaterialResource(material, allocator.Allocate("material", path), slot, family,
                built.Main, inputs.Count);
            materialResources[path] = resource;
            return resource;
        }

        uint SlotFor(string path)
        {
            var name = path.ToLowerInvariant();
            var slot = StingrayHash.Murmur32(name);
            for (var salt = 0; usedSlots.TryGetValue(slot, out var owner) &&
                !owner.Equals(path, StringComparison.OrdinalIgnoreCase); salt++)
                slot = StingrayHash.Murmur32(name + "#" + salt.ToString(CultureInfo.InvariantCulture));
            usedSlots[slot] = path;
            return slot;
        }

        IReadOnlyList<Hd2MaterialInput> BasicInputs(GmodMaterial? material)
        {
            var basePath = Roles(material, "BaseColor");
            var bumpPath = Roles(material, "Normal") ?? Roles(material, "NormalSecondary");
            var maskPath = Roles(material, "MetallicRoughnessAO") ?? Roles(material, "Roughness");

            var albedo = Texture($"albedo|{basePath}", basePath, "albedo", false, () =>
            {
                var source = Source(basePath);
                return source is null
                    ? Hd2TextureComposer.Solid(8, 8, 150, 150, 150)
                    : Hd2TextureComposer.Opaque(source);
            });
            var normal = Texture($"normal|{bumpPath}", bumpPath, "normal", false,
                () => Source(bumpPath) ?? Hd2TextureWriter.FlatNormal());
            var mra = Texture($"mra|{basePath}|{maskPath}", maskPath ?? basePath ?? "(neutral)", "mra", false, () =>
            {
                var size = Source(basePath) ?? Source(maskPath);
                var mask = Source(maskPath);
                return Hd2TextureComposer.Map(size?.Width ?? 8, size?.Height ?? 8, (x, y) => (
                    Hd2TextureComposer.SampleChannel(mask, x, y, 0, 0),        // 金属度：缺遮罩即非金属
                    Hd2TextureComposer.SampleChannel(mask, x, y, 0, 200),      // 粗糙度：缺遮罩取偏粗糙
                    (byte)255,                                                  // AO
                    (byte)255));
            });

            var semantics = Hd2MaterialWriter.Semantics(Hd2MaterialFamily.Basic);
            return
            [
                new Hd2MaterialInput(semantics[0], mra),
                new Hd2MaterialInput(semantics[1], albedo),
                new Hd2MaterialInput(semantics[2], normal)
            ];
        }

        IReadOnlyList<Hd2MaterialInput> AlphaClipInputs(GmodMaterial? material)
        {
            var basePath = Roles(material, "BaseColor");
            var bumpPath = Roles(material, "Normal") ?? Roles(material, "NormalSecondary");
            var maskPath = Roles(material, "MetallicRoughnessAO") ?? Roles(material, "Roughness");

            var nar = Texture($"nar|{bumpPath}|{maskPath}", bumpPath ?? "(neutral)", "nar", false, () =>
            {
                var source = Source(bumpPath);
                var normal = source is null ? Hd2TextureWriter.FlatNormal() : Hd2TextureComposer.Opaque(source);
                var mask = Source(maskPath);
                return Hd2TextureComposer.WithMaskedAlpha(normal, mask, 0, 200);
            });
            var clip = Texture($"opacity|{basePath}", basePath ?? "(neutral)", "opacity", false, () =>
            {
                var source = Source(basePath);
                return source is null
                    ? Hd2TextureComposer.Solid(8, 8, 255, 255, 255)
                    : Hd2TextureComposer.Gray(source, 3);
            });
            var colorMetal = Texture($"metal|{basePath}|{maskPath}", basePath ?? "(neutral)",
                "base_color_metal", true, () =>
                {
                    var source = Source(basePath) ?? Hd2TextureComposer.Solid(8, 8, 150, 150, 150);
                    return Hd2TextureComposer.WithMaskedAlpha(Hd2TextureComposer.Opaque(source),
                        Source(maskPath), 0, 0);
                });

            var semantics = Hd2MaterialWriter.Semantics(Hd2MaterialFamily.AlphaClip);
            return
            [
                new Hd2MaterialInput(semantics[0], nar),
                new Hd2MaterialInput(semantics[1], clip),
                new Hd2MaterialInput(semantics[2], colorMetal)
            ];
        }

        ulong Texture(string key, string? sourcePath, string role, bool preserveAlpha,
            Func<Hd2TextureImage> compose)
        {
            if (textureResources.TryGetValue(key, out var cached)) return cached.Id;
            cancellationToken.ThrowIfCancellationRequested();
            var image = Hd2TextureComposer.PadToBlocks(compose());
            var built = Hd2TextureWriter.Build(image, preserveAlpha, cancellationToken);
            var resource = new TextureResource(key, sourcePath ?? "(neutral)", role, allocator.Allocate("texture", key),
                built.Main, built.Gpu, image.Width, image.Height, built.MipCount, built.Format);
            textureResources[key] = resource;
            return resource.Id;
        }

        Hd2TextureImage? Source(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var key = Normalize(path);
            if (!textureIndex.TryGetValue(key, out var texture))
            {
                ReportTextureOnce(key, "is referenced by a material but is not present in the addon");
                return null;
            }
            if (texture.Rgba is null || texture.Width <= 0 || texture.Height <= 0 ||
                texture.Rgba.Length < texture.Width * texture.Height * 4L)
            {
                ReportTextureOnce(key, $"could not be decoded ({texture.DecodeIssue ?? "no pixel data"})");
                return null;
            }
            return new Hd2TextureImage(texture.Width, texture.Height, texture.Rgba);
        }

        void ReportTextureOnce(string path, string reason)
        {
            if (!reportedTextures.Add(path)) return;
            issues.Add(new GmodConversionIssue("TextureNotConverted", path,
                $"The texture {reason}; the material falls back to a neutral channel value.", GmodIssueSeverity.Warning));
        }

        string? Roles(GmodMaterial? material, string role) => material?.Textures
            .FirstOrDefault(texture => string.Equals(texture.Role, role, StringComparison.OrdinalIgnoreCase))?.SourcePath;

    }

    /// <summary>Source 的 $alphatest/$translucent/$alphamask 决定用带裁切的材质族。</summary>
    public static Hd2MaterialFamily ResolveFamily(GmodMaterial? material)
    {
        if (material is null) return Hd2MaterialFamily.Basic;
        foreach (var key in new[] { "$alphatest", "$translucent", "$alphamask" })
        {
            var parameter = material.Parameters.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (parameter is not null && IsEnabled(parameter.Value)) return Hd2MaterialFamily.AlphaClip;
        }
        return Hd2MaterialFamily.Basic;
    }

    private static bool IsEnabled(string value)
    {
        var token = value.Trim().Trim('"').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (token.Length == 0) return false;
        var first = token[0].TrimStart('?', '!');
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number > 0
            : first.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().ToLowerInvariant();

    /// <summary>模组清单名：项目 ID + 部位，便于在模组库里区分 body / helmet 两个包。</summary>
    private static string PackageName(string projectId, GmodPartKind kind) =>
        $"{projectId} {(kind == GmodPartKind.Helmet ? "helmet" : "body")} (GMOD)";

    /// <summary>稳定的模组 GUID：同一项目 + 同一 Archive ID 重复转换会得到同一个 GUID，导入即更新。</summary>
    private static string DerivePackageGuid(string projectId, ulong archiveId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"hd2mimnt-gmod|{projectId}|{archiveId:x16}")));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    /// <summary>一份 Unit 载荷与它的统计；同一份载荷可以被多个 Published Unit 共享。</summary>
    private sealed record UnitPayload(byte[] Main, byte[] Gpu, int MeshCount, int VertexCount, int TriangleCount);

    private sealed record TextureResource(string Key, string SourcePath, string Role, ulong Id, byte[] Main,
        byte[] Gpu, int Width, int Height, int MipCount, string Format)
    {
        public GmodStingrayTextureResource Report =>
            new(SourcePath, Role, Id, Width, Height, MipCount, Format);
    }

    private sealed record MaterialResource(GmodMaterial? Material, ulong Id, uint Slot, Hd2MaterialFamily Family,
        byte[] Bytes, int TextureCount)
    {
        public string SourcePath => Material?.Path ?? "(missing)";

        public GmodStingrayMaterialResource Report =>
            new(SourcePath, Id, Slot, Family == Hd2MaterialFamily.AlphaClip ? "alphaclip" : "basic",
                TextureCount, Bytes.Length);
    }
}
