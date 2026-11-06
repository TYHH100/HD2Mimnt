using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2Mimnt.ModelConversion.Gmod.Geometry;
using HD2Mimnt.ModelConversion.Gmod.Materials;
using HD2Mimnt.ModelConversion.Gmod.Packaging;
using HD2Mimnt.ModelConversion.Gmod.Parts;
using HD2Mimnt.ModelConversion.Gmod.Physics;
using HD2Mimnt.ModelConversion.Gmod.Rig;
using HD2Mimnt.ModelConversion.Gmod.Switches;
using HD2Mimnt.ModelConversion.Gmod.Targets;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Gmod;

/// <summary>
/// GMOD（Source 引擎）模型 → HD2 IAS 独立护甲系统转换入口。
///
/// HD2 把护甲拆成身体与头盔两套 Unit，而 GMOD 的 MDL 把头与身体放在同一个骨架/模型里，
/// 因此默认按骨骼权重把它拆成 body / helmet 两个完整 IAS 包（见 <see cref="GmodPartSplitter"/>）。
///
/// 完整保留：全部 bodygroup 备选 × 全部 skin 家族的网格、顶点/法线/UV/切线/骨骼权重、
/// 全部骨骼与 jigglebone 参数、全部 VMT 参数与 VTF 原始字节（并尽力解码 PNG）、
/// 全部 addon 源文件；产出 IAS v6 free-parts 开关、HD2IRG1 rig、HD2PHY1 动骨资源与可核对的报告。
///
/// 同时为每个已发布 Unit 写出游戏原生 Stingray 资源（<c>.unit</c> / <c>.material</c> / 贴图），
/// 打进 <c>9ba626afa44a3aa3.patch_0</c> 三件套，并给出标准 V1 <c>manifest.json</c> 与
/// <c>hd2mimnt.json</c> 扩展——<c>package/</c> 目录本身就是一个可直接导入模组库的 data 模组。
/// </summary>
public sealed class GmodIasConverter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>从 addon（GMA 或已解压目录）读取并转换。</summary>
    public async Task<GmodConversionResult> ConvertAsync(ISourceAssetProvider provider, string mdlPath,
        GmodConversionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var package = await SourceModelPackage.LoadAsync(provider, mdlPath, cancellationToken).ConfigureAwait(false);
        return Convert(package, options, provider, cancellationToken);
    }

    /// <summary>转换已加载的 Source 模型包；provider 为空时不复制原始源文件。</summary>
    public GmodConversionResult Convert(SourceModelPackage package, GmodConversionOptions options,
        ISourceAssetProvider? provider = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        IasFreePartsCodec.ValidateId(options.ProjectId);
        IasFreePartsCodec.ValidateId(options.EffectiveIsolationId);
        IasFreePartsCodec.ValidateId(options.EffectiveManifestId);

        var model = package.Model;
        var meshSet = GmodGeometryBuilder.BuildMeshes(model, cancellationToken);
        var split = GmodPartSplitter.Split(meshSet, cancellationToken);
        var (materials, textures, materialIssues) = GmodMaterialBuilder.Build(package, cancellationToken);
        var physicsSource = JiggleBoneConverter.Convert(meshSet.Bones, package.PhysicsData, cancellationToken);

        var issues = new List<GmodConversionIssue>();
        issues.AddRange(package.Issues.Select(issue => new GmodConversionIssue(issue.Code, issue.AssetPath, issue.Message,
            GmodIssueSeverity.Info)));
        issues.AddRange(meshSet.Issues);
        issues.AddRange(materialIssues);
        issues.AddRange(physicsSource.Issues);
        issues.AddRange(split.Issues);

        var files = new List<GmodOutputFile>();
        void Add(string path, string kind, byte[] data) =>
            files.Add(new GmodOutputFile(path, kind, data, System.Convert.ToHexStringLower(SHA256.HashData(data))));

        // 1. Shared materials: every VMT parameter, texture reference and channel role (written once per conversion).
        Add("materials/materials.json", "materials", JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "HD2GmodMaterials1",
            model = model.ModelPath,
            material_count = materials.Count,
            texture_count = textures.Count,
            source_material_paths = package.MaterialPaths.OrderBy(entry => entry.Key)
                .ToDictionary(entry => entry.Key.ToString(CultureInfo.InvariantCulture), entry => entry.Value),
            materials = materials.Select(material => new
            {
                material.Path, material.Shader, material.IsPatch, material.Include,
                material.Parameters, material.Textures, material.ChannelMap
            }),
            textures = textures.Select(texture => new
            {
                texture.Path, texture.Width, texture.Height, texture.Format, texture.MipCount,
                texture.IsCubeMap, Png = texture.Png is null ? null : GmodMaterialBuilder.PngPath(texture.Path),
                texture.DecodeIssue
            })
        }, Json));

        // 2. Decoded textures plus the original VTF bytes (shared by every part).
        foreach (var texture in textures)
        {
            if (texture.Png is not null) Add(GmodMaterialBuilder.PngPath(texture.Path), "texture", texture.Png);
        }
        foreach (var asset in package.Textures.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            Add("textures/raw/" + GmodMaterialBuilder.SafePath(asset.Key), "texture-raw", asset.Value.RawData.ToArray());
        foreach (var asset in package.Materials.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            Add("materials/raw/" + GmodMaterialBuilder.SafePath(asset.Key), "material-raw", asset.Value.RawData.ToArray());

        // 3. Verbatim jigglebone parameters (all 34 floats + boing for every physics bone); shared by both parts.
        Add("physics/jiggle-parameters.json", "physics-metadata", JsonSerializer.SerializeToUtf8Bytes(
            meshSet.Bones.Where(bone => bone.Jiggle is not null).Select(bone => new
            {
                index = bone.Index, bone.Name, parent = bone.Parent,
                flags = bone.Jiggle!.Flags.ToString(), procedural_type = bone.ProceduralType,
                parameters = bone.Jiggle, boing = bone.Jiggle.Boing
            }), Json));

        // 4. Every part (body / helmet / whole model) becomes a self-contained IAS bundle.
        //    选了目标护甲时，没有对应目标的部位不产出：那只会写出一个指向不存在 Archive 的包，
        //    而且会退化成「接管整套 Unit」（每个选项都复制一份整身模型，很快撞上 512 Unit 上限）。
        var selections = ResolveSelections(options, split).ToList();
        if (options.Targets is { Count: > 0 })
        {
            foreach (var missing in selections.Where(selection => selection.Target is null))
                issues.Add(new GmodConversionIssue("TargetArmorRequiredForPart", model.ModelPath,
                    $"The {missing.Kind.ToString().ToLowerInvariant()} part was skipped because no target armour of that " +
                    "domain was selected; pick one for both domains to publish the whole model.",
                    GmodIssueSeverity.Warning));
            selections = selections.Where(selection => selection.Target is not null).ToList();
            if (selections.Count == 0)
                throw new IasFormatException("None of the selected target armours matches this model's content domain.");
        }

        var partResults = new List<GmodConversionPart>();
        var partReports = new List<GmodPartReport>();
        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            partResults.Add(BuildPart(selection));
        }

        // 5. All addon source files, byte-for-byte, with an explicit path index to avoid name collisions.
        var rawCount = CopyRawSource();

        // 5b. 独立骨架运行时配套包：rigbin / hd2phys / difference / isolation 打进 .hd2irpack.zip，
        // 与 package/ 的 data 模组配套——没有它游戏侧只有模型资源、没有骨架与物理。
        var runtimePack = GmodRuntimePackBuilder.Build(options.EffectiveIasProjectId, options.ProjectId,
            partResults, files);
        foreach (var file in runtimePack.Files) Add(file.Path, file.Kind, file.Data.ToArray());

        var primary = partResults[0];
        var report = new GmodConversionReport(options.ProjectId, model.ModelPath, meshSet.Bones.Count,
            primary.Geometry.Meshes.Count, primary.Geometry.Meshes.Sum(mesh => mesh.Vertices.Length),
            primary.Geometry.Meshes.Sum(mesh => mesh.TriangleCount), materials.Count, textures.Count,
            textures.Count(texture => texture.Png is not null), primary.Switches.Count,
            primary.Switches.Sum(@switch => @switch.Options.Count),
            partResults.Sum(part => part.Physics.Sum(profile => profile.Summary.Chains)),
            partResults.Sum(part => part.Physics.Sum(profile => profile.Summary.Joints)),
            partResults.Sum(part => part.Physics.Count), partResults.Sum(part => part.Rig.Profiles.Count),
            rawCount, issues.AsReadOnly(), partReports.ToArray())
        {
            RuntimePack = runtimePack.FileName,
            RuntimePackBytes = runtimePack.Files.Sum(file => file.Data.Length),
            RuntimePackGroupId = runtimePack.GroupId
        };

        // 6. Human-readable report and a checksum manifest of everything emitted above.
        Add("conversion-report.json", "report", JsonSerializer.SerializeToUtf8Bytes(report, Json));
        if (partResults.Count > 1)
        {
            Add("split.json", "split", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "HD2GmodPartSplit1",
                model = model.ModelPath,
                reason = "HD2 equips body armour and helmets as separate IAS archives; the GMOD model keeps head and body in one MDL.",
                head_bones = split.HeadBones.Order().Select(index => meshSet.Bones[index].Name).ToArray(),
                parts = partResults.Select(part => new
                {
                    part = part.Kind.ToString().ToLowerInvariant(),
                    prefix = part.Prefix,
                    domain = IasFreePartsCodec.DomainName(part.Domain),
                    archive = "0x" + part.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
                    mesh_count = part.Geometry.Meshes.Count,
                    triangle_count = part.Geometry.Meshes.Sum(mesh => mesh.TriangleCount),
                    switch_count = part.Switches.Count,
                    manifest = part.Prefix + "manifest.json"
                }).ToArray()
            }, Json));
        }
        Add("manifest.json", "manifest", JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "HD2GmodIasConversion1",
            project_id = options.ProjectId,
            archive = "0x" + options.Binding.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
            domain = IasFreePartsCodec.DomainName(options.Binding.Domain),
            part_split = options.PartSplit.ToString().ToLowerInvariant(),
            runtime_pack = report.RuntimePack,
            runtime_pack_group = report.RuntimePackGroupId,
            model = model.ModelPath,
            self = "manifest.json",
            file_count = files.Count + 1,
            files = files.Select(file => new { file.Path, file.Kind, size = file.Data.Length, file.Sha256 })
        }, Json));

        return new GmodConversionResult(primary.Differences, primary.Rig, primary.Geometry, materials, textures,
            primary.Switches, primary.Physics, files.AsReadOnly(), report, partResults.AsReadOnly());

        GmodConversionPart BuildPart(PartSelection selection)
        {
            var binding = selection.Binding;
            var partOptions = options with
            {
                Binding = binding,
                IsolationId = selection.IsolationId,
                ManifestId = selection.ManifestId,
                // 整身模型只接管目标护甲的一个槽位，其余槽位写成零面积占位。
                CarrierUnitIds = selection.Target is null ? null : [selection.Target.CarrierUnitId]
            };
            if (selection.Target is null)
                issues.Add(new GmodConversionIssue("TargetArmorNotSelected", model.ModelPath,
                    "No target armour was selected, so the published routes point at the Archive / Native Unit IDs you " +
                    "typed in. In game the model only appears when those IDs belong to an existing armour.",
                    GmodIssueSeverity.Warning));
            else
                issues.Add(new GmodConversionIssue("TargetArmor", model.ModelPath,
                    $"Target armour {selection.Target.DisplayName} (0x{selection.Target.ArchiveId:x16}): the model takes over " +
                    $"Unit 0x{selection.Target.CarrierUnitId:x16} and {selection.Target.CleanupUnitIds.Count} other Unit(s) of the " +
                    "same armour are routed to zero-area placeholders.", GmodIssueSeverity.Info));
            var plan = IasSwitchPlanner.Plan(model, partOptions, selection.AllowedAxes, cancellationToken);
            var geometry = GmodGeometryBuilder.Assemble(model, meshSet, plan.Options, selection.MeshFilter,
                cancellationToken);
            var (rig, rigIssues) = GmodRigBuilder.Build(geometry.Bones, plan.PublishedUnitIds, partOptions, cancellationToken);
            issues.AddRange(plan.Issues);
            issues.AddRange(geometry.Issues);
            issues.AddRange(rigIssues);

            var stem = $"{partOptions.EffectiveIasProjectId}.{binding.ArchiveId:x16}";
            // IAS companion files: difference / isolation / rigbin, all cross-validated by the existing builder.
            foreach (var companion in IasCompanionBuilder.Build(partOptions.EffectiveIasProjectId,
                partOptions.EffectiveIsolationId, plan.Differences, binding, rig, plan.CleanupRoutes, cancellationToken))
                Add(selection.Prefix + "ias/" + companion.Name, "ias", companion.Data.ToArray());
            Add(selection.Prefix + "geometry/model.hd2geom", "geometry", GmodGeometryCodec.Write(geometry, cancellationToken));

            // Physics: one HD2PHY1 profile per published Unit, restricted to the chains that belong to this part.
            var profiles = new List<GmodPhysicsProfile>();
            var chains = FilterChains(physicsSource.Chains, selection.Kind, split, meshSet.Bones);
            if (options.IncludePhysics && chains.Count > 0)
            {
                var partSlot = plan.Differences.Parts[0].Slot.ToString();
                foreach (var unitId in plan.PublishedUnitIds.OrderBy(id => id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var document = new Hd2PhysDocument(unitId, physicsSource.PaletteSlots, physicsSource.Bones,
                        chains, options.FixedDt);
                    var (bytes, summary) = Hd2PhysCodec.Write(document, cancellationToken);
                    var name = $"{stem}.{partSlot}.{unitId:x16}.hd2phys";
                    Add(selection.Prefix + "physics/" + name, "physics", bytes);
                    profiles.Add(new GmodPhysicsProfile(name, unitId, bytes, summary));
                }
                Add(selection.Prefix + "physics/" + stem + ".hd2physpack.json", "physics-manifest",
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schema = "HD2IntegratedPhysicsPack1",
                        project_id = partOptions.ProjectId,
                        part = selection.Kind.ToString().ToLowerInvariant(),
                        solver_scope = "single_consumer_part_v1",
                        rig = new { file = stem + ".rigbin" },
                        chain_consumers = chains.ToDictionary(chain => chain.Name, _ => partSlot),
                        units = profiles.Select(profile => new
                        {
                            part_slot = partSlot,
                            unit_id = "0x" + profile.UnitId.ToString("x16", CultureInfo.InvariantCulture),
                            physics_file = profile.FileName,
                            physics_summary = profile.Summary
                        })
                    }, Json));
            }
            else if (options.IncludePhysics && selection.Kind == GmodPartKind.Body && physicsSource.Chains.Count > 0)
            {
                issues.Add(new GmodConversionIssue("NoPhysicsProfiles", model.ModelPath,
                    "No HD2PHY1 spring chains could be derived. All jigglebone parameters and .phy bytes are still preserved verbatim."));
            }

            var part = new GmodConversionPart(selection.Prefix, selection.Kind, binding.Domain, binding.ArchiveId,
                plan.Differences, rig, geometry, plan.Switches, profiles.AsReadOnly())
            {
                CleanupRoutes = plan.CleanupRoutes,
                Target = selection.Target
            };

            // 游戏原生 Stingray 资源 + 可直接导入模组库的 data 模组包（package/ 目录）。
            var package = GmodStingrayPackager.Build(part, materials, textures, cancellationToken);
            foreach (var file in package.Files)
                Add(selection.Prefix + file.Path, file.Kind, file.Data.ToArray());
            issues.AddRange(package.Issues);
            Add(selection.Prefix + "runnable.json", "runnable", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "HD2GmodRunnable1",
                project_id = partOptions.ProjectId,
                part = selection.Kind.ToString().ToLowerInvariant(),
                domain = IasFreePartsCodec.DomainName(binding.Domain),
                package = GmodStingrayPackager.PackageDirectory,
                manifest = GmodStingrayPackager.PackageDirectory + "/manifest.json",
                guid = package.Summary.Guid,
                target_armor = selection.Target is null ? null : new
                {
                    code = selection.Target.Code,
                    name = selection.Target.Name,
                    display_name = selection.Target.DisplayName,
                    archive = "0x" + selection.Target.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
                    carrier_unit = "0x" + selection.Target.CarrierUnitId.ToString("x16", CultureInfo.InvariantCulture),
                    armour_unit_count = selection.Target.UnitIds.Count
                },
                cleanup_unit_count = package.Summary.CleanupUnitCount,
                patch = GmodStingrayPackager.PackageDirectory + "/data/" + package.Summary.PatchFile,
                unit_count = package.Summary.UnitCount,
                material_count = package.Summary.MaterialCount,
                texture_count = package.Summary.TextureCount,
                patch_bytes = package.Summary.PatchBytes,
                gpu_bytes = package.Summary.GpuBytes,
                stream_bytes = package.Summary.StreamBytes,
                max_palette_size = package.Summary.MaxPaletteSize,
                import = "Import the package folder as a data mod (manifest.json at its root) and enable it in a " +
                    "profile; the IAS companion files next to this report supply the armour rig.",
                units = package.Units.Select(unit => new
                {
                    unit_id = "0x" + unit.UnitId.ToString("x16", CultureInfo.InvariantCulture),
                    unit.OptionId,
                    label = unit.OptionLabel,
                    mesh_count = unit.MeshCount,
                    vertex_count = unit.VertexCount,
                    triangle_count = unit.TriangleCount,
                    placeholder = unit.Placeholder
                })
            }, Json));

            Add(selection.Prefix + "part-report.json", "part-report", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "HD2GmodPart1",
                part = selection.Kind.ToString().ToLowerInvariant(),
                domain = IasFreePartsCodec.DomainName(binding.Domain),
                archive = "0x" + binding.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
                target_armor = selection.Target?.DisplayName,
                cleanup_unit_count = package.Summary.CleanupUnitCount,
                mesh_count = geometry.Meshes.Count,
                vertex_count = geometry.Meshes.Sum(mesh => mesh.Vertices.Length),
                triangle_count = geometry.Meshes.Sum(mesh => mesh.TriangleCount),
                unit_count = package.Summary.UnitCount,
                material_count = package.Summary.MaterialCount,
                texture_count = package.Summary.TextureCount,
                patch_bytes = package.Summary.PatchBytes,
                switches = plan.Switches.Select(@switch => new
                {
                    @switch.Id,
                    @switch.DisplayName,
                    kind = @switch.Kind.ToString(),
                    slot = @switch.Slot.ToString(),
                    options = @switch.Options.Select(option => option.DisplayName).ToArray()
                }),
                physics_chains = chains.Select(chain => chain.Name).ToArray()
            }, Json));

            partReports.Add(new GmodPartReport(selection.Prefix.TrimEnd('/'), selection.Kind.ToString(),
                IasFreePartsCodec.DomainName(binding.Domain),
                "0x" + binding.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
                geometry.Meshes.Count, geometry.Meshes.Sum(mesh => mesh.Vertices.Length),
                geometry.Meshes.Sum(mesh => mesh.TriangleCount), plan.Switches.Count,
                plan.Switches.Sum(@switch => @switch.Options.Count), rig.Profiles.Count,
                profiles.Sum(profile => profile.Summary.Chains), profiles.Count)
            {
                UnitCount = package.Summary.UnitCount,
                MaterialResourceCount = package.Summary.MaterialCount,
                TextureResourceCount = package.Summary.TextureCount,
                PackageBytes = package.Summary.PatchBytes + package.Summary.GpuBytes + package.Summary.StreamBytes,
                TargetArmor = selection.Target?.DisplayName ?? "",
                CleanupUnitCount = package.Summary.CleanupUnitCount
            });

            // 本包已写入的文件（含 part-report）+ 自身 manifest。
            var partFileCount = files.Count(file => file.Path.StartsWith(selection.Prefix, StringComparison.Ordinal) &&
                IsPartFile(file.Kind)) + 1;

            // 部位清单固定叫 part-manifest.json：部位目录里的 manifest.json 只能属于可运行的模组包，
            // 否则用户很容易把审计用的部位清单误当成模组清单导入。
            const string partManifestName = "part-manifest.json";
            Add(selection.Prefix + partManifestName, "part-manifest", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "HD2GmodPartBundle1",
                project_id = partOptions.ProjectId,
                part = selection.Kind.ToString().ToLowerInvariant(),
                domain = IasFreePartsCodec.DomainName(binding.Domain),
                archive = "0x" + binding.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
                model = model.ModelPath,
                self = partManifestName,
                file_count = partFileCount,
                files = files.Where(file => file.Path.StartsWith(selection.Prefix, StringComparison.Ordinal) &&
                        IsPartFile(file.Kind))
                    .Select(file => new
                    {
                        path = selection.Prefix.Length == 0 ? file.Path : file.Path[selection.Prefix.Length..],
                        file.Kind,
                        size = file.Data.Length,
                        file.Sha256
                    })
            }, Json));

            return part;
        }

        int CopyRawSource()
        {
            if (!options.IncludeRawSource || provider is null) return 0;
            var index = new List<object>();
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            var count = 0;
            foreach (var path in provider.EnumeratePaths(cancellationToken).OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!provider.TryRead(path, out var data, cancellationToken)) continue;
                if (total + data.Length > options.MaxRawSourceBytes)
                {
                    issues.Add(new GmodConversionIssue("RawSourceBudgetExceeded", path,
                        $"Raw source copy stopped at {total} bytes (limit {options.MaxRawSourceBytes}); remaining addon files are not copied."));
                    break;
                }
                var relative = GmodMaterialBuilder.SafePath(path);
                var output = "source/" + relative;
                if (!claimed.Add(output))
                {
                    var digest = System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12];
                    var extension = Path.GetExtension(relative);
                    output = extension.Length == 0
                        ? "source/" + relative + "_" + digest
                        : "source/" + relative[..^extension.Length] + "_" + digest + extension;
                    claimed.Add(output);
                }
                Add(output, "source", data);
                index.Add(new { source_path = path, output_path = output, size = data.Length });
                total += data.Length;
                count++;
            }
            Add("source/index.json", "source-index", JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "HD2GmodSourceIndex1",
                count,
                addon = provider is GmaAssetProvider gma
                    ? new { gma.Archive.Name, gma.Archive.Author, gma.Archive.Description, gma.Archive.AddonVersion }
                    : null,
                files = index
            }, Json));
            return count;
        }
    }

    private static bool IsPartFile(string kind) =>
        kind is "ias" or "geometry" or "physics" or "physics-manifest" or "part-report" or "runnable"
            or "patch" or "patch-gpu" or "patch-stream" or "mod-manifest" or "mod-metadata" or "package-index";

    /// <summary>按 <see cref="GmodPartSplit"/> 决定产出哪些包：拆分模式下 body/ 与 helmet/ 各一份。</summary>
    private static IReadOnlyList<PartSelection> ResolveSelections(GmodConversionOptions options, GmodPartSplitResult split)
    {
        var bodyTarget = options.Targets?.FirstOrDefault(target => target.Domain == IasContentDomain.Body);
        var helmetTarget = options.Targets?.FirstOrDefault(target => target.Domain == IasContentDomain.Helmet);
        var selections = new List<PartSelection>();
        switch (options.PartSplit)
        {
            case GmodPartSplit.None:
            {
                var target = options.Binding.Domain == IasContentDomain.Helmet ? helmetTarget : bodyTarget;
                selections.Add(new PartSelection("",
                    options.Binding.Domain == IasContentDomain.Helmet ? GmodPartKind.Helmet : GmodPartKind.Body,
                    target is null ? options.Binding : BindingFor(target),
                    options.IsolationId, options.ManifestId, null, null, target));
                break;
            }

            case GmodPartSplit.Body:
                selections.Add(BodySelection(""));
                break;

            case GmodPartSplit.Helmet:
                selections.Add(HelmetSelection(""));
                break;

            default:
                // 拆分模式：头部子树里有网格才产出头盔包，否则只产出身体包并保留诊断。
                selections.Add(BodySelection("body/"));
                if (split.HasHelmet) selections.Add(HelmetSelection("helmet/"));
                break;
        }

        // 绝不产出空包：只有骨骼被头发/帽子占据这类极端模型才可能出现一侧为空。
        return selections.Where(selection => selection.MeshFilter is null || selection.MeshFilter.Count > 0).ToArray();

        PartSelection BodySelection(string prefix) => new(prefix, GmodPartKind.Body,
            bodyTarget is null ? options.Binding with { Domain = IasContentDomain.Body } : BindingFor(bodyTarget),
            options.IsolationId, options.ManifestId,
            split.MeshIds(GmodPartKind.Body), split.AxisKeys(GmodPartKind.Body, options.IncludeSkinSwitch), bodyTarget);

        PartSelection HelmetSelection(string prefix)
        {
            var binding = helmetTarget is not null
                ? BindingFor(helmetTarget)
                : new IasArchiveBinding(options.HelmetArchiveId ?? options.DeriveHelmetArchiveId(),
                    IasContentDomain.Helmet, options.Binding.Equipment);
            // 未显式指定 ID 时按各自的 Archive 派生，天然不会和身体包撞名；显式指定才需要加后缀区分。
            return new PartSelection(prefix, GmodPartKind.Helmet, binding,
                options.IsolationId is { } isolationId ? Suffix(isolationId, "-helmet") : null,
                options.ManifestId is { } manifestId ? Suffix(manifestId, "-helmet") : null,
                split.MeshIds(GmodPartKind.Helmet), split.AxisKeys(GmodPartKind.Helmet, options.IncludeSkinSwitch),
                helmetTarget);
        }
    }

    /// <summary>
    /// 目标护甲 → IAS 绑定。每份 equipment 记录都声明同一份 Unit 闭包：
    /// 载体与占位 Unit 都必须同时属于所有 equipment 记录，IAS 校验器才接受这些路由。
    /// </summary>
    private static IasArchiveBinding BindingFor(GmodArmorTarget target) =>
        new(target.ArchiveId, target.Domain,
            target.EquipmentKeys.Select(key => new IasEquipmentBinding(key, target.UnitIds)).ToArray());

    /// <summary>IAS 标识符上限 63 字符，派生部位 id 时截断而不是报错。</summary>
    private static string Suffix(string id, string suffix) =>
        id.Length + suffix.Length <= 63 ? id + suffix : id[..(63 - suffix.Length)] + suffix;

    /// <summary>头盔包只保留植根于头部子树的动骨链，身体包保留其余链。</summary>
    private static IReadOnlyList<Hd2PhysChain> FilterChains(IReadOnlyList<Hd2PhysChain> chains,
        GmodPartKind kind, GmodPartSplitResult split, IReadOnlyList<GmodBone> bones)
    {
        if (split.HeadBones.Count == 0) return chains;
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bone in bones) indexByName.TryAdd(bone.Name, bone.Index);
        return chains.Where(chain =>
        {
            if (chain.Joints.Count == 0) return true;
            var root = chain.Joints[0].Bone;
            var isHead = indexByName.TryGetValue(root, out var index) && split.HeadBones.Contains(index);
            return kind == GmodPartKind.Helmet ? isHead : !isHead;
        }).ToArray();
    }

    private sealed record PartSelection(
        string Prefix,
        GmodPartKind Kind,
        IasArchiveBinding Binding,
        string? IsolationId,
        string? ManifestId,
        IReadOnlySet<string>? MeshFilter,
        IReadOnlySet<string>? AllowedAxes,
        GmodArmorTarget? Target);
}
