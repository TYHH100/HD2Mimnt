using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasCompileRequest(
    string ModelId,
    string? SourceName,
    IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> Resources,
    IReadOnlyDictionary<string, byte[]> Members,
    IasCompanionData Data,
    IasRouting Routing,
    IasGameData? Game,
    string AvatarArchive = "18235e0c9ec0e636",
    string PreferredBodyType = "Slim",
    IReadOnlyList<IasMorphSourceProgram>? MorphPrograms = null,
    IReadOnlyDictionary<(ulong Type, ulong Id), IasResource>? AdditionalResources = null);

public sealed record IasCompiledModel(
    string Name,
    IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> Owned,
    IasValue Catalog,
    IasValue Registration,
    IReadOnlyList<IasPhysicsDomain> Domains,
    IReadOnlyList<ulong> MaterialIds,
    IReadOnlyList<ulong> VisibleMaterialIds,
    IReadOnlyDictionary<string, IasGeometryStats> Geometry,
    IasNativeMorphProof? MorphProof = null,
    IReadOnlyDictionary<(ulong Type, ulong Id), IasResource>? MorphMaterials = null);

public sealed record IasNativeGraphNode(string Name, int Parent, IasPose LocalRest, IasPose Rest);

public sealed record IasGripSocket(string Name, IasPose Rest);

public sealed record IasBinding(string Group, int[] Mapping, IasPose? Basis, string? Slot, string? Native, string? Choice);

/// <summary>上游 <c>ias_builder.compile_model</c> 的 IAS 私有模型/目录/物理生成。</summary>
public static class IasModelCompiler
{
    private const string Format = "hd2cl-manual-adapter-1";
    private const ulong AvatarUnit = 5556372446766824087;
    private const int CatalogInlineLimit = 4_190_208;

    public static IasCompiledModel Compile(IasCompileRequest request)
    {
        var data = request.Data;
        var routing = request.Routing;
        IasBinary.Require(Regex.IsMatch(data.Metadata.Id, "\\A[0-9a-f]{32}\\z"), "unstable IAS group identity");
        var prefix = "mods/hd2qm/" + request.ModelId + "/v1/";
        var hints = new List<string>();
        foreach (var member in data.Metadata.Members)
        {
            var archive = member.Archive.ToLowerInvariant();
            if (archive.StartsWith("0x", StringComparison.Ordinal)) archive = archive[2..];
            hints.Add(archive);
        }
        hints.Add(request.AvatarArchive);
        hints.Add("9ba626afa44a3aa3");
        var loaded = new Dictionary<(ulong, ulong), IasResource>();
        IasResource Resolve(ulong kind, ulong id, bool nativeFallback, bool nativeAvatar = false)
        {
            if (request.Resources.TryGetValue((kind, id), out var value)) return value;
            if (request.AdditionalResources is not null && request.AdditionalResources.TryGetValue((kind, id), out value)) return value;
            if (loaded.TryGetValue((kind, id), out value)) return value;
            if (request.Game is not null)
            {
                foreach (var archive in (nativeAvatar ? [request.AvatarArchive] : hints.Distinct()))
                {
                    try
                    {
                        value = request.Game.Resource(archive, kind, id);
                        loaded[(kind, id)] = value;
                        return value;
                    }
                    catch (KeyNotFoundException)
                    {
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
            }
            if (nativeFallback) throw new InvalidDataException($"模型数据检查：effective deployed resource absent: {kind:x16}/{id:x16}");
            throw new InvalidDataException($"模型数据检查：effective deployed resource absent: {kind:x16}/{id:x16}");
        }

        var allUnits = routing.Published.ToDictionary(uid => uid,
            uid => Resolve(IasResourceTypes.Unit, ulong.Parse(uid, NumberStyles.HexNumber), false), StringComparer.Ordinal);
        var externalBones = new Dictionary<string, (ulong Id, IasResource Resource)>(StringComparer.Ordinal);
        foreach (var (uid, resource) in allUnits)
        {
            var reference = BitConverter.ToUInt64(resource.Toc, 8);
            if (reference != 0) externalBones[uid] = (reference, Resolve(IasResourceTypes.Bones, reference, true));
        }
        var materialIds = new HashSet<ulong>();
        foreach (var resource in allUnits.Values) materialIds.UnionWith(IasUnit.IndexedMaterials(resource));

        var known = new Dictionary<uint, string>();
        foreach (var name in data.Sources.Select(source => source.Name)
                     .Concat(data.RawProfiles.Values.SelectMany(profile => profile.TargetBones.Select(node => node.Name)))
                     .Concat(["StingrayEntityRoot", "FbxAxisSystem_ConvertNode", "skeleton", "game_mesh"]))
        {
            var hash = IasHash.Murmur32(name);
            IasBinary.Require(!known.TryGetValue(hash, out var existing) || existing == name, "explicit Rig name hash collision");
            known[hash] = name;
        }
        var avatar = Resolve(IasResourceTypes.Unit, AvatarUnit, false, nativeAvatar: true);
        var (nativeGraph, sockets) = NativeSourceGraph(IasUnit.ReadGraph(avatar.Toc, known), data.Sources);
        var units = allUnits.ToDictionary(pair => pair.Key,
            pair => new IasUnit(pair.Key, pair.Value, known,
                externalBones.TryGetValue(pair.Key, out var bones) ? bones.Resource : null), StringComparer.Ordinal);
        foreach (var unit in units.Values)
            IasBinary.Require(unit.Refs[0] == 0 && unit.Refs[2] == 0 && unit.Refs[3] == 0 && unit.Refs[4] == 0,
                unit.Id + ": unsupported external Unit dependency");
        // 各 Unit 的几何分类互不共享状态；并行度限制在 4，避免大模组同时保留过多顶点缓存。
        var unitKeys = units.Keys.ToList();
        var stats = new IasGeometryStats[unitKeys.Count];
        RunParallel(unitKeys.Count, 4, index => stats[index] = units[unitKeys[index]].Geometry());
        var geometry = new Dictionary<string, IasGeometryStats>(StringComparer.Ordinal);
        for (var index = 0; index < unitKeys.Count; index++) geometry[unitKeys[index]] = stats[index];

        var profiles = data.RenderProfiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var bindings = new Dictionary<string, IasBinding>(StringComparer.Ordinal);
        foreach (var shared in data.Shared)
        foreach (var (uid, consumer) in shared.Consumers)
            bindings[uid] = new IasBinding(shared.Canonical, consumer.Mapping, consumer.Basis,
                consumer.Slot, consumer.Native, consumer.Choice);
        foreach (var group in data.Groups.Where(group => !group.Virtual))
            bindings[group.Id] = new IasBinding(group.Id,
                Enumerable.Range(1, profiles[group.Id].Nodes.Count).ToArray(), null, null, null, null);

        var nativeTypes = ResolveNativeBodyTypes(request, routing, Resolve);
        var legacy = LegacyRoutes(data, routing, units, geometry, profiles, bindings, request.PreferredBodyType);
        var assembled = AssembleRoutes(data, routing, units, geometry, profiles, nativeTypes, legacy);

        var title = request.SourceName ?? data.Metadata.Id;
        title = Regex.Replace(title, "\\s+替换.*$", "");
        title = title.TrimEnd(' ', '▲');
        var usedGroups = assembled.Selected.Where(uid => bindings.ContainsKey(uid))
            .Select(uid => bindings[uid].Group).ToHashSet(StringComparer.Ordinal);
        var groups = data.Groups.Where(group => usedGroups.Contains(group.Id)).ToList();
        if (groups.Any(group => !group.AnimationOnly)) title += " ▲";
        var packageName = prefix + "library";
        var packageId = IasHash.Murmur64(packageName);

        var domains = new List<IasPhysicsDomain>();
        foreach (var group in groups.Where(group => !group.AnimationOnly))
        {
            var profile = data.RawProfiles[group.Id];
            IasBinary.Require(group.File is not null, "missing physics file");
            IasBinary.Require(request.Members.TryGetValue(group.File!, out var physics), "missing physics bytes");
            domains.Add(new IasPhysicsDomain(group.Id, profile.TargetBones.Count,
                profile.TargetBones.Select(node => node.Parent).ToList(),
                IasCompanionReader.PreviewRig(profile), physics!));
        }
        IasNativeDataSpec? spec = null;
        IReadOnlyDictionary<string, IasNativeDeclaration> declarations = new Dictionary<string, IasNativeDeclaration>();
        if (domains.Count > 0)
        {
            var encoded = IasNativeModelData.Encode(prefix + "catalog", request.ModelId, domains);
            spec = encoded.Spec;
            declarations = IasNativeModelData.Declarations(prefix + "catalog", domains);
            groups = groups.Select(group => group.AnimationOnly ? group : group with { Native = declarations[group.Id] }).ToList();
        }
        var activeGroups = groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var dynamicBindings = bindings.Where(pair => activeGroups.Contains(pair.Value.Group))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var owned = new Dictionary<(ulong Type, ulong Id), IasResource>();
        var viewMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["body"] = new(StringComparer.Ordinal), ["arms"] = new(StringComparer.Ordinal)
        };
        var displayUnits = new Dictionary<string, string>(StringComparer.Ordinal);
        var drive = new Dictionary<string, IasDriveRecord>(StringComparer.Ordinal);
        var privateBones = new Dictionary<ulong, ulong>();
        var selectedUnits = assembled.Selected.Order(StringComparer.Ordinal).ToList();
        foreach (var uid in selectedUnits)
        {
            if (!externalBones.TryGetValue(uid, out var external) || privateBones.ContainsKey(external.Id)) continue;
            var name = prefix + "bones/" + Format + "/" + $"{external.Id:x16}" + "/"
                       + IasHash.Sha256Hex(external.Resource.Toc, external.Resource.Gpu, external.Resource.Stream);
            var privateId = IasHash.Murmur64(name);
            privateBones[external.Id] = privateId;
            owned[(IasResourceTypes.Bones, privateId)] = external.Resource;
        }

        UnitViewResult BuildUnitViews(string uid)
        {
            var unit = units[uid];
            var profile = profiles[uid];
            var cached = unit.Resource;
            if (externalBones.TryGetValue(uid, out var external))
            {
                var privateId = privateBones[external.Id];
                var toc = (byte[])unit.Resource.Toc.Clone();
                BitConverter.GetBytes(privateId).CopyTo(toc, 8);
                cached = unit.Resource with { Toc = toc };
            }
            var record = new IasDriveRecord(uid, unit.Graph.Count,
                Enumerable.Range(1, profile.Nodes.Count).ToList(),
                profile.Nodes.Select(node => node.Name).ToList());
            var assetName = prefix + "view/" + Format + "/" + uid + "/"
                            + IasHash.Sha256Hex(cached.Toc, cached.Gpu, cached.Stream) + "/tp";
            var viewId = IasHash.Murmur64(assetName);
            IasBinary.Require(!units.ContainsKey($"{viewId:x16}"), "derived Unit collides with source Unit");
            var display = $"{viewId:x16}";
            if (assembled.HeadUnits.Contains(uid))
                return new UnitViewResult(uid, display, record, cached, viewId, []);

            var labels = unit.Graph.ToDictionary(node => node.Hash, node => node.Name!);
            var resolved = new List<string>();
            foreach (var node in profile.Nodes)
            {
                var label = node.Inherit ? resolved[node.Parent - 1] : data.Sources[node.Source - 1].Name;
                resolved.Add(label);
                labels[IasHash.Murmur32(node.Name)] = label;
            }
            var (buffers, _) = unit.PartitionLabels([labels, null]);
            var roles = new List<(string Role, ulong ViewId, IasResource Resource)>(buffers.Count);
            foreach (var (role, gpu) in buffers)
            {
                var roleAssetName = prefix + "view/" + Format + "/" + uid + "/"
                                    + IasHash.Sha256Hex(cached.Toc, gpu) + "/" + role;
                var roleViewId = IasHash.Murmur64(roleAssetName);
                IasBinary.Require(!units.ContainsKey($"{roleViewId:x16}"), "derived Unit collides with source Unit");
                roles.Add((role, roleViewId, new IasResource(cached.Toc, gpu, unit.Resource.Stream,
                    cached.EntryMetadata, cached.Origin)));
            }
            return new UnitViewResult(uid, display, record, cached, viewId, roles);
        }

        // 每个 Unit 的视图与分区互不共享状态；并行度限制在 3，避免同时保留过多 GPU 掩码副本。
        var unitViews = new UnitViewResult[selectedUnits.Count];
        RunParallel(unitViews.Length, 3, index => unitViews[index] = BuildUnitViews(selectedUnits[index]));
        foreach (var result in unitViews)
        {
            drive[result.Uid] = result.Record;
            displayUnits[result.Uid] = result.DisplayUnit;
            owned[(IasResourceTypes.Unit, result.TpViewId)] = result.Cached;
            drive[result.DisplayUnit] = result.Record;
            foreach (var (role, roleViewId, resource) in result.Roles)
            {
                owned[(IasResourceTypes.Unit, roleViewId)] = resource;
                viewMaps[role][result.Uid] = $"{roleViewId:x16}";
                drive[$"{roleViewId:x16}"] = result.Record;
            }
        }
        var views = new Dictionary<string, IasValue>(StringComparer.Ordinal);
        foreach (var (role, mapping) in viewMaps)
            views[role] = ViewValue(role, mapping, assembled, profiles, nativeGraph, data, drive,
                request.ModelId, title, $"{packageId:x16}", packageName);
        var bound = BindTpGrips(profiles, groups, sockets, data.Sources);
        var model = ModelValue(request, data, assembled, nativeGraph, displayUnits, drive, views,
            bound.Profiles, bound.Groups, dynamicBindings, bound.Used,
            title, $"{packageId:x16}", packageName);
        var catalog = IasValue.Obj(
            ("version", IasValue.Of(2L)),
            ("models", IasValue.Of([model])),
            ("original_resources", IasValue.Obj(routing.Initial.Keys.Order(StringComparer.Ordinal)
                .Select(uid => (uid, IasValue.Of(true))).ToArray())),
            ("bridge_resources", IasValue.Obj()),
            ("native_domains", IasValue.Obj(routing.Domains.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, IasValue.Of(pair.Value == "helmet" ? "HELMET" : "BODY"))).ToArray())),
            ("preview_replacement", IasValue.Of(false)));
        var registrationEntries = new List<(string, IasValue)>
        {
            ("protocol", IasValue.Of(1L)),
            ("id", IasValue.Of(request.ModelId)),
            ("name", IasValue.Of(title)),
            ("module", IasValue.Of(prefix + "catalog")),
            ("package", IasValue.Of($"{packageId:x16}")),
            ("materials", IasValue.Of(materialIds.Order().Select(id => IasValue.Of($"{id:x16}")))),
        };
        if (spec is not null)
            registrationEntries.Add(("native_data", IasValue.Obj(
                ("version", IasValue.Of(1L)), ("module", IasValue.Of(spec.Module)), ("sha256", IasValue.Of(spec.Sha256)))));
        if (groups.Count > 0 && domains.Count == 0)
            registrationEntries.Add(("animation_only", IasValue.Of(true)));
        var registration = IasValue.Obj(registrationEntries.ToArray());
        var visibleMaterials = assembled.Selected.SelectMany(uid => geometry[uid].RenderMaterials)
            .Distinct().Order().ToList();
        var compiled = new IasCompiledModel(title, owned, catalog, registration, domains,
            materialIds.Order().ToList(), visibleMaterials, geometry);
        if (request.MorphPrograms is not { Count: > 0 }) return compiled;

        var morphMaterials = new Dictionary<(ulong Type, ulong Id), IasResource>();
        foreach (var pair in request.Resources.Concat(request.AdditionalResources ?? Enumerable.Empty<KeyValuePair<(ulong, ulong), IasResource>>())
                     .Where(pair => pair.Key.Item1 == IasResourceTypes.Material))
            morphMaterials[pair.Key] = pair.Value;
        foreach (var id in materialIds)
        {
            if (morphMaterials.ContainsKey((IasResourceTypes.Material, id))) continue;
            try { morphMaterials[(IasResourceTypes.Material, id)] = Resolve(IasResourceTypes.Material, id, false); }
            catch (InvalidDataException) { }
            catch (KeyNotFoundException) { }
            catch (FileNotFoundException) { }
        }
        var morphUnitIds = request.MorphPrograms.SelectMany(program => program.Document.Meshes)
            .Select(mesh => mesh.UnitId).Distinct(StringComparer.Ordinal).ToList();
        var morphUnits = new Dictionary<string, IasUnit>(StringComparer.Ordinal);
        foreach (var uid in morphUnitIds)
        {
            if (!allUnits.TryGetValue(uid, out var resource)) continue;
            morphUnits[uid] = new IasUnit(uid, resource, known,
                externalBones.TryGetValue(uid, out var bones) ? bones.Resource : null, allowUnnamed: true);
        }
        var converted = IasNativeMorph.Apply(compiled, request.MorphPrograms, morphUnits, morphMaterials);
        return converted.Compiled with { MorphMaterials = converted.Materials };
    }

    private static (Dictionary<string, IasProfileTable> Profiles, List<IasCompanionGroup> Groups,
        Dictionary<string, IasGripSocket> Used) BindTpGrips(
        IReadOnlyDictionary<string, IasProfileTable> profiles, List<IasCompanionGroup> groups,
        IReadOnlyDictionary<string, IasGripSocket> sockets, IReadOnlyList<IasProjectedSource> sources)
    {
        var used = new Dictionary<string, IasGripSocket>(StringComparer.Ordinal);
        IasProfileTable Bind(IasProfileTable table)
        {
            var grips = table.Grips.ToList();
            for (var index = 0; index < grips.Count; index++)
            {
                var grip = grips[index];
                var side = grip.Side == 0 ? "r" : "l";
                var handName = side + "_hand";
                var hand = table.Nodes[grip.Hand - 1];
                if (!sockets.TryGetValue(handName, out var socket) || hand.Inherit || hand.Source <= 0
                    || hand.Source > sources.Count || sources[hand.Source - 1].Name != handName
                    || grips.Count(value => value.Side == grip.Side) != 1)
                    continue;
                var parent = hand.Parent;
                var matched = true;
                foreach (var joint in new[] { side + "_elbow", side + "_shoulder" })
                {
                    while (parent != 0 && table.Nodes[parent - 1].Name != joint) parent = table.Nodes[parent - 1].Parent;
                    if (parent == 0)
                    {
                        matched = false;
                        break;
                    }
                    parent = table.Nodes[parent - 1].Parent;
                }
                if (!matched) continue;
                grips[index] = grip with { NativeSocket = socket.Name };
                used[handName] = socket;
            }
            return table with { Grips = grips };
        }

        var bound = profiles.ToDictionary(pair => pair.Key, pair => Bind(pair.Value), StringComparer.Ordinal);
        for (var index = 0; index < groups.Count; index++)
            groups[index] = groups[index] with { Profile = Bind(groups[index].Profile) };
        return (bound, groups, used);
    }

    private static IasValue ViewValue(string role, IReadOnlyDictionary<string, string> mapping,
        AssembledRoutes assembled, IReadOnlyDictionary<string, IasProfileTable> profiles,
        IReadOnlyList<IasNativeGraphNode> nativeGraph, IasCompanionData data,
        IReadOnlyDictionary<string, IasDriveRecord> drive, string modelId, string name, string package, string packageName)
    {
        var entries = new List<(string, IasValue)>();
        var parts = assembled.Parts.Select(part => PartValue(part, mapping.ContainsKey)).ToList();
        entries.Add(("id", IasValue.Of(modelId)));
        entries.Add(("name", IasValue.Of(name)));
        entries.Add(("package", IasValue.Of(package)));
        entries.Add(("package_name", IasValue.Of(packageName)));
        entries.Add(("sources", SourcesValue(data.Sources)));
        entries.Add(("source_index", SourceIndexValue(data.Sources)));
        entries.Add(("source_native_graph", NativeGraphValue(nativeGraph)));
        entries.Add(("ground", IasValue.Of([IasValue.Of(0L), IasValue.Of(0L), IasValue.Of(0L)])));
        entries.Add(("parts", IasValue.Of(parts)));
        entries.Add(("base", Strings(assembled.Base.Where(mapping.ContainsKey))));
        if (assembled.BodyRoutes is not null)
            entries.Add(("ias_body_routes", IasValue.Obj(
                ("version", IasValue.Of(1L)),
                ("variants", VariantsValue(assembled.BodyRoutes, mapping.ContainsKey)))));
        entries.Add(("profiles", ProfilesValue(mapping.Keys.ToDictionary(uid => uid, uid => profiles[uid],
            StringComparer.Ordinal))));
        entries.Add(("display_units", role == "body" ? Strings(mapping) : IasValue.Obj()));
        entries.Add(("first_person", role == "arms" ? Strings(mapping) : IasValue.Obj()));
        entries.Add(("allow_empty", IasValue.Of(true)));
        if (mapping.Count == 0) entries.Add(("passthrough", IasValue.Of(true)));
        entries.Add(("content_domains", assembled.ContentDomains.Contains("BODY")
            ? IasValue.Of([IasValue.Of("BODY")]) : IasValue.Of(Array.Empty<IasValue>())));
        entries.Add(("display_drive", DriveValue(drive)));
        return IasValue.Obj(entries.ToArray());
    }

    private static IasValue ModelValue(IasCompileRequest request, IasCompanionData data, AssembledRoutes assembled,
        IReadOnlyList<IasNativeGraphNode> nativeGraph, IReadOnlyDictionary<string, string> displayUnits,
        IReadOnlyDictionary<string, IasDriveRecord> drive, IReadOnlyDictionary<string, IasValue> views,
        IReadOnlyDictionary<string, IasProfileTable> profiles, IReadOnlyList<IasCompanionGroup> groups,
        IReadOnlyDictionary<string, IasBinding> bindings, IReadOnlyDictionary<string, IasGripSocket> tpGrips,
        string name, string package, string packageName)
    {
        var entries = new List<(string, IasValue)>
        {
            ("id", IasValue.Of(request.ModelId)),
            ("name", IasValue.Of(name)),
            ("package", IasValue.Of(package)),
            ("package_name", IasValue.Of(packageName)),
            ("sources", SourcesValue(data.Sources)),
            ("source_index", SourceIndexValue(data.Sources)),
            ("source_native_graph", NativeGraphValue(nativeGraph)),
            ("profiles", ProfilesValue(profiles)),
            ("base", Strings(assembled.Base)),
            ("parts", IasValue.Of(assembled.Parts.Select(part => PartValue(part, _ => true)).ToList())),
            ("head_units", Strings(assembled.HeadUnits)),
            ("ground", IasValue.Of([IasValue.Of(0L), IasValue.Of(0L), IasValue.Of(0L)])),
            ("display_units", Strings(displayUnits)),
            ("first_person", IasValue.Obj()),
            ("variation_mode", IasValue.Of("ias_authored_parts")),
            ("legacy_parts", LegacyPartsValue(assembled.LegacyParts)),
            ("content_domains", IasValue.Of(assembled.ContentDomains.Select(IasValue.Of).ToList())),
        };
        if (assembled.BodyRoutes is not null)
            entries.Add(("ias_body_routes", IasValue.Obj(
                ("version", IasValue.Of(1L)),
                ("source_types", IasValue.Obj(IasBodyCatalog.NativeSourceBodyTypes
                    .OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
                    .Select(pair => (pair.Key, IasValue.Of(pair.Value))).ToArray())),
                ("variants", VariantsValue(assembled.BodyRoutes, _ => true)),
                ("recipe_evidence", IasValue.Of(Array.Empty<IasValue>())),
                ("metadata_sha256", IasValue.Nil.Instance))));
        entries.Add(("display_drive", DriveValue(drive)));
        entries.Add(("views", IasValue.Obj(views.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
            .Select(pair => (pair.Key, pair.Value)).ToArray())));
        if (groups.Count > 0)
        {
            var dynamic = new List<(string, IasValue)>
            {
                ("version", IasValue.Of(2L)),
                ("engine", IasValue.Of("native_companion_v1")),
                ("groups", IasValue.Of(groups.Select(GroupValue).ToList())),
                ("bindings", IasValue.Obj(bindings.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
                    .Select(pair => (pair.Key, BindingValue(pair.Value))).ToArray())),
            };
            if (groups.Any(group => group.ClothingPose is not null))
                dynamic.Add(("clothing_pose_enabled", IasValue.Of(true)));
            entries.Add(("dynamic_rig", IasValue.Obj(dynamic.ToArray())));
        }
        if (tpGrips.Count > 0)
            entries.Add(("tp_grip_nodes", IasValue.Obj(tpGrips.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
                .Select(pair => (pair.Key, IasValue.Obj(("name", IasValue.Of(pair.Value.Name)),
                    ("rest", PoseValue(pair.Value.Rest))))).ToArray())));
        return IasValue.Obj(entries.ToArray());
    }

    private static IasValue GroupValue(IasCompanionGroup group)
    {
        var entries = new List<(string, IasValue)>
        {
            ("id", IasValue.Of(group.Id)),
            ("virtual", IasValue.Of(group.Virtual)),
        };
        if (group.File is not null) entries.Add(("file", IasValue.Of(group.File)));
        entries.Add(("profile", ProfileTableValue(group.Profile)));
        entries.Add(("original_rig", IasValue.Of(group.OriginalRig ?? "")));
        if (group.PhysicsSha256 is not null) entries.Add(("physics_sha256", IasValue.Of(group.PhysicsSha256)));
        if (group.Fingerprint is not null) entries.Add(("fingerprint", IasValue.Of(group.Fingerprint)));
        if (group.File is not null) entries.Add(("palette_slots", IasValue.Of(group.PaletteSlots)));
        if (group.AnimationOnly) entries.Add(("animation_only", IasValue.Of(true)));
        if (group.ClothingPose is not null) entries.Add(("clothing_pose", ClothingPoseValue(group.ClothingPose)));
        if (group.Native is not null) entries.Add(("native", IasValue.Obj(
            ("key", IasValue.Of(group.Native.Key)),
            ("directory", IasValue.Of(group.Native.Directory)),
            ("count", IasValue.Of(group.Native.Count)))));
        return IasValue.Obj(entries.ToArray());
    }

    private static IasValue ClothingPoseValue(IasClothingPoseProgram program)
    {
        var rules = program.Rules.Select(rule => IasValue.Obj(
            ("source", IasValue.Of(rule.Source)), ("target", IasValue.Of(rule.Target)),
            ("secondary", IasValue.Of(rule.Secondary)), ("reference", IasValue.Of(rule.Reference)),
            ("secondary_reference", IasValue.Of(rule.SecondaryReference)), ("mode", IasValue.Of(rule.Mode)),
            ("influence", IasValue.Of(rule.Influence)), ("neutral", Floats(rule.Neutral)),
            ("secondary_neutral", Floats(rule.SecondaryNeutral)),
            ("axes", IasValue.Of(rule.Axes.Select(axis => IasValue.Obj(
                ("invert", IasValue.Of(axis.Invert)), ("invert_secondary", IasValue.Of(axis.InvertSecondary)),
                ("secondary_axis", IasValue.Of(axis.SecondaryAxis)),
                ("points", IasValue.Of(axis.Points.Select(Floats).ToList())))).ToList())))).ToList();
        return IasValue.Obj(
            ("version", IasValue.Of(program.Version)),
            ("rules", IasValue.Of(rules)),
            ("author_from_runtime", PoseValue(program.AuthorFromRuntime)),
            ("source_sha256", IasValue.Of(program.SourceSha256)),
            ("profile_sha256", IasValue.Of(program.ProfileSha256)),
            ("original_hex", IasValue.Of(program.OriginalHex)));
    }

    private static IasValue Identity(IasBinding binding)
        => IasValue.Obj(("group", IasValue.Of(binding.Group)));

    private static IasValue BindingValue(IasBinding binding)
    {
        var entries = new List<(string, IasValue)> { ("group", IasValue.Of(binding.Group)) };
        if (binding.Mapping.Length > 0)
            entries.Add(("mapping", IasValue.Of(binding.Mapping.Select(value => IasValue.Of(value)).ToList())));
        if (binding.Basis is not null) entries.Add(("basis", PoseValue(binding.Basis)));
        if (binding.Slot is not null) entries.Add(("slot", IasValue.Of(binding.Slot)));
        if (binding.Native is not null) entries.Add(("native", IasValue.Of(binding.Native)));
        if (binding.Choice is not null) entries.Add(("choice", IasValue.Of(binding.Choice)));
        return IasValue.Obj(entries.ToArray());
    }

    private static IasValue ProfilesValue(IReadOnlyDictionary<string, IasProfileTable> profiles)
        => IasValue.Obj(profiles.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
            .Select(pair => (pair.Key, ProfileTableValue(pair.Value))).ToArray());

    private static IasValue ProfileTableValue(IasProfileTable table)
        => IasValue.Obj(
            ("nodes", IasValue.Of(table.Nodes.Select(node => IasValue.Obj(
                ("name", IasValue.Of(node.Name)), ("parent", IasValue.Of(node.Parent)),
                ("source", IasValue.Of(node.Source)), ("slot", IasValue.Of(node.Slot)),
                ("inherit", IasValue.Of(node.Inherit)), ("scale", IasValue.Of(node.Scale)),
                ("local_rest", PoseValue(node.LocalRest)), ("rest", PoseValue(node.Rest)),
                ("ias_flags", IasValue.Of((long)node.IasFlags)))).ToList())),
            ("grips", IasValue.Of(table.Grips.Select(grip =>
            {
                var entries = new List<(string, IasValue)>
                {
                    ("hand", IasValue.Of(grip.Hand)), ("side", IasValue.Of(grip.Side))
                };
                entries.AddRange(PoseEntries(grip.Rest));
                if (grip.NativeSocket is not null) entries.Add(("native_socket", IasValue.Of(grip.NativeSocket)));
                return IasValue.Obj(entries.ToArray());
            }).ToList())));

    private static IasValue SourcesValue(IReadOnlyList<IasProjectedSource> sources)
        => IasValue.Of(sources.Select(source => IasValue.Obj(
            ("name", IasValue.Of(source.Name)), ("parent", IasValue.Of(source.Parent)),
            ("local_rest", PoseValue(source.LocalRest)), ("ias_flags", IasValue.Of((long)source.IasFlags)),
            ("q", Floats(source.World.Q)), ("p", Floats(source.World.P)))).ToList());

    private static IasValue SourceIndexValue(IReadOnlyList<IasProjectedSource> sources)
        => IasValue.Obj(sources.Select((source, index) => (source.Name, IasValue.Of(index + 1))).ToArray());

    private static IasValue NativeGraphValue(IReadOnlyList<IasNativeGraphNode> nodes)
        => IasValue.Of(nodes.Select(node => IasValue.Obj(
            ("name", IasValue.Of(node.Name)), ("parent", IasValue.Of(node.Parent)),
            ("local_rest", PoseValue(node.LocalRest)),
            ("q", Floats(node.Rest.Q)), ("p", Floats(node.Rest.P)))).ToList());

    private static IasValue DriveValue(IReadOnlyDictionary<string, IasDriveRecord> drive)
        => IasValue.Obj(
            ("version", IasValue.Of(1L)),
            ("resources", IasValue.Obj(drive.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
                .Select(pair => (pair.Key, IasValue.Obj(
                    ("profile", IasValue.Of(pair.Value.Profile)),
                    ("scene_nodes", IasValue.Of(pair.Value.SceneNodes)),
                    ("nodes", IasValue.Of(pair.Value.Nodes.Select(IasValue.Of).ToList())),
                    ("names", IasValue.Of(pair.Value.Names.Select(IasValue.Of).ToList()))))).ToArray())));

    private static IasValue PartValue(PartState part, Func<string, bool> keep)
    {
        var options = part.Options.Select(option => IasValue.Obj(
            ("id", IasValue.Of(option.Id)),
            ("name", IasValue.Of(option.Name)),
            ("units", Strings(option.Units.Where(keep)))));
        return IasValue.Obj(
            ("id", IasValue.Of(part.Id)),
            ("name", IasValue.Of(part.Name)),
            ("selected", IasValue.Of(part.Selected)),
            ("options", IasValue.Of(options.ToList())));
    }

    private static IasValue VariantsValue(BodyRoutesState routes, Func<string, bool> keep)
    {
        var variants = new List<(string, IasValue)>();
        foreach (var (body, variant) in routes.Variants.OrderBy(pair => pair.Key, IasCodePointComparer.Instance))
        {
            var parts = variant.Parts.Select(part =>
            {
                var options = part.Options.Select(option => IasValue.Obj(
                    ("units", Strings(option.Units.Where(keep)))));
                return IasValue.Obj(("options", IasValue.Of(options.ToList())));
            });
            variants.Add((body, IasValue.Obj(
                ("base", Strings(variant.Base.Where(keep))),
                ("parts", IasValue.Of(parts.ToList())))));
        }
        return IasValue.Obj(variants.ToArray());
    }

    private static IasValue LegacyPartsValue(IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> parts)
        => IasValue.Obj(parts.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
            .Select(part => (part.Key, IasValue.Obj(part.Value.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
                .Select(option => (option.Key, IasValue.Obj(option.Value.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
                    .Select(choice => (choice.Key, IasValue.Of(choice.Value))).ToArray()))).ToArray())))
            .ToArray());

    private static IasValue Strings(IEnumerable<string> values)
        => IasValue.Of(values.OrderBy(value => value, IasCodePointComparer.Instance).Select(IasValue.Of).ToList());

    private static IasValue Strings(IReadOnlyDictionary<string, string> values)
        => IasValue.Obj(values.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
            .Select(pair => (pair.Key, IasValue.Of(pair.Value))).ToArray());

    private static IasValue Floats(IReadOnlyList<double> values)
        => IasValue.Of(values.Select(IasValue.Of).ToList());

    private static IasValue PoseValue(IasPose pose) => IasValue.Obj(PoseEntries(pose));

    private static (string, IasValue)[] PoseEntries(IasPose pose)
        => [("q", Floats(pose.Q)), ("p", Floats(pose.P))];

    private static (IReadOnlyList<IasNativeGraphNode>, Dictionary<string, IasGripSocket>) NativeSourceGraph(
        IReadOnlyList<IasSceneNode> native, IReadOnlyList<IasProjectedSource> sources)
    {
        var sourceNames = sources.Select(source => source.Name).ToHashSet(StringComparer.Ordinal);
        var retained = native.Where(node => node.Index != 0 && node.Name is not null && sourceNames.Contains(node.Name)).ToList();
        IasBinary.Require(retained.Select(node => node.Name!).ToHashSet(StringComparer.Ordinal).SetEquals(sourceNames)
                          && retained.Count == sourceNames.Count,
            "original native Avatar does not uniquely resolve all sources");
        var indices = retained.Select((node, index) => (node.Index, Value: index + 1))
            .ToDictionary(pair => pair.Index, pair => pair.Value);
        var output = new List<IasNativeGraphNode>(retained.Count);
        foreach (var node in retained)
        {
            var parent = node.Parent;
            while (parent != 0 && !indices.ContainsKey(parent)) parent = native[parent].Parent;
            var parentWorld = parent != 0 ? native[parent].World : native[0].World;
            var local = IasAffine.Product(IasAffine.Inverse(parentWorld), node.World);
            output.Add(new IasNativeGraphNode(node.Name!, indices.GetValueOrDefault(parent, 0),
                IasAffine.Pose(local), IasAffine.Pose(node.World)));
        }
        var sockets = new Dictionary<string, IasGripSocket>(StringComparer.Ordinal);
        foreach (var side in new[] { "r", "l" })
        {
            var hand = side + "_hand";
            var socketName = "attach_hand_" + side;
            var candidates = native.Where(node => node.Hash == IasHash.Murmur32(socketName)).ToList();
            if (!sourceNames.Contains(hand) || candidates.Count != 1) continue;
            if (native[candidates[0].Parent].Name != hand) continue;
            var child = candidates[0];
            var parent = native[child.Parent];
            var rest = IasAffine.Pose(IasAffine.Product(IasAffine.Inverse(parent.World), child.World));
            if (rest.P.Sum(value => value * value) > 1e-12) sockets[hand] = new IasGripSocket(socketName, rest);
        }
        return (output, sockets);
    }

    private static Dictionary<string, string> ResolveNativeBodyTypes(IasCompileRequest request,
        IasRouting routing, Func<ulong, ulong, bool, bool, IasResource> resolve)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        var stockTypes = StockBodyTypes(routing.BodyOwners);
        foreach (var (native, domain) in routing.Domains)
        {
            if (domain != "body") continue;
            var raw = resolve(IasResourceTypes.Unit, ulong.Parse(native, NumberStyles.HexNumber), true, false);
            var kind = IasUnit.ReadCustomization(raw.Toc).GetValueOrDefault("body_type", "");
            var shape = kind.StartsWith("HelldiverCustomizationBodyType_", StringComparison.Ordinal)
                ? kind["HelldiverCustomizationBodyType_".Length..] : kind;
            types[native] = shape;
            if (stockTypes.TryGetValue(native, out var stock))
            {
                if (stock != shape) types[native] = stock;
            }
            else if (raw.Toc.Length >= 80 && BitConverter.ToUInt32(raw.Toc, 76) == 0
                     && IasBodyCatalog.NativeBodyTypes.TryGetValue(native, out var table)
                     && table is "Slim" or "Stocky"
                     && IasBodyCatalog.NativeSourceBodyTypes.TryGetValue(native, out var sourceType)
                     && table == sourceType)
            {
                types[native] = table;
            }
        }
        return types;
    }

    private static Dictionary<string, string> StockBodyTypes(IReadOnlyList<IasRouteBodyOwner> owners)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            if (!IasBodyCatalog.StockRecipes.TryGetValue(owner.Archive, out var looks) || looks.Count == 0) continue;
            var pairs = looks.ToDictionary(pair => pair.Key, pair => (
                Stocky: pair.Value.Stocky.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
                Slim: pair.Value.Slim.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)),
                StringComparer.Ordinal);
            var known = owner.Sources.Where(source => pairs.Values.Any(pair => pair.Stocky.Contains(source) || pair.Slim.Contains(source)))
                .ToHashSet(StringComparer.Ordinal);
            var choices = pairs.ContainsKey(owner.Equipment) ? [pairs[owner.Equipment]] : pairs.Values.ToArray();
            var projections = new List<(HashSet<string> Stocky, HashSet<string> Slim)>();
            foreach (var pair in choices)
            {
                if (!known.All(source => pair.Stocky.Contains(source) || pair.Slim.Contains(source))) continue;
                var projection = (known.Where(pair.Stocky.Contains).ToHashSet(StringComparer.Ordinal),
                    known.Where(pair.Slim.Contains).ToHashSet(StringComparer.Ordinal));
                if (!projections.Any(item => item.Stocky.SetEquals(projection.Item1) && item.Slim.SetEquals(projection.Item2)))
                    projections.Add(projection);
            }
            IasBinary.Require(projections.Count == 1, "native body appearance has incompatible or ambiguous source membership");
            var (stocky, slim) = projections[0];
            foreach (var uid in known)
            {
                var shape = stocky.Contains(uid) && slim.Contains(uid) ? "Any" : stocky.Contains(uid) ? "Stocky" : "Slim";
                IasBinary.Require(!types.TryGetValue(uid, out var existing) || existing == shape, "native body source owners disagree");
                types[uid] = shape;
            }
        }
        return types;
    }

    /// <summary>并行执行时保持业务异常原样抛出，避免调用方只看到 AggregateException。</summary>
    private static void RunParallel(int count, int degree, Action<int> body)
    {
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = degree }, body);
        }
        catch (AggregateException error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(error.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
    }

    private sealed record IasDriveRecord(string Profile, int SceneNodes, IReadOnlyList<int> Nodes, IReadOnlyList<string> Names);

    private sealed record UnitViewResult(string Uid, string DisplayUnit, IasDriveRecord Record,
        IasResource Cached, ulong TpViewId,
        IReadOnlyList<(string Role, ulong ViewId, IasResource Resource)> Roles);

    private sealed class PartState(string id, string name, int selected)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public int Selected { get; } = selected;
        public List<OptionState> Options { get; } = [];
    }

    private sealed class OptionState(string id, string name, IEnumerable<string> units)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public List<string> Units { get; } = units.ToList();
    }

    private sealed record BodyOptionState(List<string> Units);

    private sealed record BodyPartState(List<BodyOptionState> Options);

    private sealed record BodyVariantState(List<string> Base, List<BodyPartState> Parts);

    private sealed record BodyRoutesState(Dictionary<string, BodyVariantState> Variants);

    private static LegacyResult LegacyRoutes(IasCompanionData data, IasRouting routing,
        IReadOnlyDictionary<string, IasUnit> units, IReadOnlyDictionary<string, IasGeometryStats> geometry,
        IReadOnlyDictionary<string, IasProfileTable> profiles, IReadOnlyDictionary<string, IasBinding> bindings,
        string preferred)
    {
        try
        {
            return LegacyCore(data, routing, units, geometry, profiles, bindings, preferred);
        }
        catch (InvalidDataException)
        {
            return new LegacyResult([], [], new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal));
        }
    }

    private static LegacyResult LegacyCore(IasCompanionData data, IasRouting routing,
        IReadOnlyDictionary<string, IasUnit> units, IReadOnlyDictionary<string, IasGeometryStats> geometry,
        IReadOnlyDictionary<string, IasProfileTable> profiles, IReadOnlyDictionary<string, IasBinding> bindings,
        string preferred)
    {
        var hidden = geometry.Where(pair => pair.Value.AnimatedTriangles == 0).Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = new Dictionary<(string Slot, string Choice), List<string>>();
        foreach (var shared in data.Shared)
        foreach (var (uid, consumer) in shared.Consumers)
        {
            if (!units.ContainsKey(uid) || hidden.Contains(uid)) continue;
            var key = (consumer.Slot, consumer.Choice);
            if (!candidates.TryGetValue(key, out var list)) candidates[key] = list = [];
            list.Add(uid);
        }
        var inactive = new HashSet<string>(StringComparer.Ordinal);
        var variantTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, pair) in candidates)
        {
            if (pair.Count == 1) continue;
            IasBinary.Require(pair.Count == 2, "ambiguous authored export group needs explicit body-type membership");
            var types = pair.Select(uid => BodyTypeEvidence(uid, units[uid], routing, bindings)).ToList();
            if (EquivalentSkin(units[pair[0]], units[pair[1]], data.RawProfiles[pair[0]], data.RawProfiles[pair[1]]))
            {
                var matching = pair.Where((_, index) => types[index] == preferred).ToList();
                var chosen = matching.Count == 1 ? matching[0] : pair.Min(StringComparer.Ordinal)!;
                foreach (var uid in pair.Where(uid => uid != chosen)) inactive.Add(uid);
            }
            else
            {
                IasBinary.Require(types.ToHashSet(StringComparer.Ordinal).SetEquals(["Slim", "Stocky"]),
                    "different exports lack verified mutually exclusive body types");
                for (var index = 0; index < pair.Count; index++) variantTypes[pair[index]] = types[index]!;
            }
        }
        if (data.Shared.Count == 0)
        {
            var scopes = new List<(string Label, HashSet<string> Ids)>
            {
                ("base", routing.Initial.Where(pair => !routing.Replaced.Contains(pair.Key)).Select(pair => pair.Value)
                    .ToHashSet(StringComparer.Ordinal))
            };
            scopes.AddRange(routing.Parts.SelectMany(part => part.Options.Select(option =>
                (part.Id + "/" + option.Id, option.Units.ToHashSet(StringComparer.Ordinal)))));
            foreach (var (_, ids) in scopes)
            {
                var typed = ids.Where(uid => units.ContainsKey(uid) && !hidden.Contains(uid)
                                             && routing.PublishedDomains.GetValueOrDefault(uid) == "body")
                    .Select(uid => (Uid: uid, Type: BodyTypeEvidence(uid, units[uid], routing, bindings))).ToList();
                typed = typed.Where(item => item.Type is "Slim" or "Stocky").ToList();
                if (typed.Select(item => item.Type).ToHashSet(StringComparer.Ordinal).SetEquals(["Slim", "Stocky"]))
                {
                    foreach (var (uid, type) in typed) variantTypes[uid] = type!;
                }
            }
        }
        var usable = units.Keys.Where(uid => profiles.ContainsKey(uid) && !hidden.Contains(uid) && !inactive.Contains(uid))
            .ToHashSet(StringComparer.Ordinal);
        var baseUnits = routing.Initial.Where(pair => !routing.Replaced.Contains(pair.Key) && usable.Contains(pair.Value))
            .Select(pair => pair.Value).Order(StringComparer.Ordinal).ToList();
        var parts = routing.Parts.Select(part => new PartState(part.Id, part.Name, part.Selected)).ToList();
        for (var index = 0; index < parts.Count; index++)
        {
            var source = routing.Parts[index];
            foreach (var option in source.Options)
                parts[index].Options.Add(new OptionState(option.Id, option.Name,
                    option.Units.Where(usable.Contains).Order(StringComparer.Ordinal)));
        }
        var legacyParts = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal);
        if (variantTypes.Count > 0)
        {
            var affected = parts.Where(part => part.Options.Any(option => option.Units.Any(variantTypes.ContainsKey))).ToList();
            var unaffected = parts.Where(part => !affected.Contains(part)).ToList();
            var optionCount = 2;
            foreach (var part in affected) optionCount *= part.Options.Count;
            IasBinary.Require(optionCount <= 64, "body-type outfit exceeds the existing 64-option receiver limit");
            var outfits = new List<(string Id, string Name, List<string> Units, string Body,
                Dictionary<string, string> Choices, Dictionary<string, int> Indices)>();
            foreach (var body in new[] { "Slim", "Stocky" })
            {
                var ranges = affected.Select(part => Enumerable.Range(0, part.Options.Count).ToList()).ToList();
                foreach (var choices in Cartesian(ranges))
                {
                    var chosen = affected.Select((part, index) => part.Options[choices[index]]).ToList();
                    var ids = baseUnits.Concat(chosen.SelectMany(option => option.Units)).ToHashSet(StringComparer.Ordinal);
                    ids = ids.Where(uid => !variantTypes.TryGetValue(uid, out var type) || type == body)
                        .ToHashSet(StringComparer.Ordinal);
                    IasBinary.Require(ids.Count > 0, "empty authored body-type outfit");
                    var suffix = string.Join(" / ", affected.Select((part, index) =>
                        part.Name + "：" + part.Options[choices[index]].Name));
                    var outfitId = body.ToLowerInvariant() + "_" + IasHash.Sha256Hex(
                        IasLua.JsonBytes(IasValue.Of(chosen.Select(option => IasValue.Of(option.Id)).ToList())))[..12];
                    outfits.Add((outfitId,
                        (body == "Slim" ? "清瘦" : "健壮") + (suffix.Length > 0 ? " / " + suffix : ""),
                        ids.Order(StringComparer.Ordinal).ToList(), body,
                        affected.Select((part, index) => (Part: part.Id, Option: chosen[index].Id))
                            .ToDictionary(pair => pair.Part, pair => pair.Option, StringComparer.Ordinal),
                        affected.Select((part, index) => (Part: part.Id, Index: choices[index]))
                            .ToDictionary(pair => pair.Part, pair => pair.Index, StringComparer.Ordinal)));
                }
            }
            var sharedUnits = outfits.Select(outfit => outfit.Units.ToHashSet(StringComparer.Ordinal))
                .Aggregate((left, right) => left.Intersect(right).ToHashSet(StringComparer.Ordinal));
            outfits = outfits.Select(outfit => outfit with
            {
                Units = outfit.Units.Where(uid => !sharedUnits.Contains(uid)).ToList()
            }).ToList();
            var defaults = affected.ToDictionary(part => part.Id, part => part.Selected - 1, StringComparer.Ordinal);
            var selectedIndex = outfits.FindIndex(outfit => outfit.Body == preferred
                && affected.All(part => outfit.Indices[part.Id] == defaults[part.Id]));
            IasBinary.Require(selectedIndex >= 0, "preferred body-type outfit is missing");
            var bodyOutfit = new PartState("body_outfit", "体型与服饰", selectedIndex + 1);
            foreach (var outfit in outfits)
                bodyOutfit.Options.Add(new OptionState(outfit.Id, outfit.Name, outfit.Units));
            baseUnits = sharedUnits.Order(StringComparer.Ordinal).ToList();
            parts = [bodyOutfit, .. unaffected];
            legacyParts["body_outfit"] = outfits.ToDictionary(outfit => outfit.Id,
                outfit => (IReadOnlyDictionary<string, string>)outfit.Choices, StringComparer.Ordinal);
        }
        var selected = new HashSet<string>(baseUnits, StringComparer.Ordinal);
        foreach (var part in parts)
        foreach (var option in part.Options)
            selected.UnionWith(option.Units);
        IasBinary.Require(selected.SetEquals(usable) && selected.Count > 0, "unrouted/empty active model");
        return new LegacyResult(baseUnits, parts, selected, legacyParts);
    }

    private static IEnumerable<int[]> Cartesian(IReadOnlyList<List<int>> ranges)
    {
        var result = new List<int[]>();
        void Walk(int index, int[] current)
        {
            if (index == ranges.Count)
            {
                result.Add((int[])current.Clone());
                return;
            }
            foreach (var value in ranges[index])
            {
                current[index] = value;
                Walk(index + 1, current);
            }
        }
        Walk(0, new int[ranges.Count]);
        return result;
    }

    private static string? BodyTypeEvidence(string uid, IasUnit unit, IasRouting routing,
        IReadOnlyDictionary<string, IasBinding> bindings)
    {
        var exported = unit.Customization.GetValueOrDefault("body_type", "");
        if (exported.EndsWith("_Slim", StringComparison.Ordinal)) return "Slim";
        if (exported.EndsWith("_Stocky", StringComparison.Ordinal)) return "Stocky";
        var natives = routing.Initial.Where(pair => pair.Value == uid).Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var part in routing.Parts)
        foreach (var option in part.Options)
            foreach (var (native, published) in option.Routes)
                if (published == uid) natives.Add(native);
        if (bindings.TryGetValue(uid, out var binding) && binding.Native is not null) natives.Add(binding.Native);
        var types = natives.Where(IasBodyCatalog.NativeBodyTypes.ContainsKey)
            .Select(native => IasBodyCatalog.NativeBodyTypes[native]).ToHashSet(StringComparer.Ordinal);
        return types.Count == 1 ? types.First() : null;
    }

    private static bool EquivalentSkin(IasUnit left, IasUnit right, IasRigProfile leftProfile, IasRigProfile rightProfile)
    {
        if (leftProfile.TargetBones.Count != rightProfile.TargetBones.Count) return false;
        for (var index = 0; index < leftProfile.TargetBones.Count; index++)
        {
            var a = leftProfile.TargetBones[index];
            var b = rightProfile.TargetBones[index];
            if (a.Name != b.Name || a.Parent != b.Parent || a.SourceIndex != b.SourceIndex
                || a.Flags != b.Flags || !a.TranslationScale.Equals(b.TranslationScale)) return false;
            if (!NearPose(a.RestLocal, b.RestLocal)) return false;
        }
        var leftMeshes = left.Meshes.Where(mesh => mesh.Lod >= 0 && !mesh.Culling).ToList();
        var rightMeshes = right.Meshes.Where(mesh => mesh.Lod >= 0 && !mesh.Culling).ToList();
        if (leftMeshes.Count != rightMeshes.Count) return false;
        var maximum = 0.0;
        var checkedVertices = 0;
        static double MaxDiff(IReadOnlyList<double> left, IReadOnlyList<double> right)
        {
            var result = 0.0;
            var count = Math.Min(left.Count, right.Count);
            for (var index = 0; index < count; index++)
                result = Math.Max(result, Math.Abs(left[index] - right[index]));
            return result;
        }
        static double[] Position(IasUnit unit, IasUnitMesh mesh, uint vertex, Dictionary<uint, double[]> cache)
        {
            if (cache.TryGetValue(vertex, out var value)) return value;
            value = unit.VertexReader.ReadStatic(mesh, vertex, 0, 0);
            cache[vertex] = value;
            return value;
        }
        for (var index = 0; index < leftMeshes.Count; index++)
        {
            var a = leftMeshes[index];
            var b = rightMeshes[index];
            if (a.Lod != b.Lod || a.Sections.Count != b.Sections.Count) return false;
            var leftComponents = left.Streams[(int)a.Stream].Components
                .Where(component => component.Kind is >= 1 and <= 5)
                .ToDictionary(component => (component.Kind, component.Index));
            var rightComponents = right.Streams[(int)b.Stream].Components
                .Where(component => component.Kind is >= 1 and <= 5)
                .ToDictionary(component => (component.Kind, component.Index));
            if (!leftComponents.Keys.ToHashSet().SetEquals(rightComponents.Keys)) return false;
            var positionsA = new Dictionary<uint, double[]>();
            var positionsB = new Dictionary<uint, double[]>();
            for (var sectionIndex = 0; sectionIndex < a.Sections.Count; sectionIndex++)
            {
                var sectionA = a.Sections[sectionIndex];
                var sectionB = b.Sections[sectionIndex];
                if (!sectionA.Faces.SequenceEqual(sectionB.Faces)) return false;
                var vertices = sectionA.Faces.Where(face => face.A != face.B && face.A != face.C && face.B != face.C)
                    .SelectMany(face => new[] { face.A, face.B, face.C }).ToHashSet();
                if (vertices.Count > 0 && sectionA.Material != sectionB.Material) return false;
                if (vertices.Count == 0) continue;
                foreach (var vertex in vertices)
                {
                    foreach (var (key, component) in leftComponents)
                    {
                        var other = rightComponents[key];
                        if (component.Format != other.Format) return false;
                        var valueA = left.VertexReader.ReadStatic(a, vertex, key.Kind, key.Index);
                        var valueB = right.VertexReader.ReadStatic(b, vertex, key.Kind, key.Index);
                        var error = MaxDiff(valueA, valueB);
                        if (error != 0 && component.Format is not (0 or 1 or 2 or 3 or 32 or 33 or 34 or 35)) return false;
                        if (error >= 2e-5) return false;
                    }
                    var influencesA = left.Influences(a, sectionA, vertex);
                    var influencesB = right.Influences(b, sectionB, vertex);
                    if (influencesA.Count != influencesB.Count) return false;
                    var positionA = Position(left, a, vertex, positionsA);
                    var positionB = Position(right, b, vertex, positionsB);
                    if (a.Rigid || b.Rigid)
                    {
                        if (a.Rigid != b.Rigid) return false;
                        if (left.Graph[influencesA[0].Scene].Hash != right.Graph[influencesB[0].Scene].Hash) return false;
                        var pointA = IasAffine.Point(left.Graph[(int)a.Transform].World, positionA);
                        var pointB = IasAffine.Point(right.Graph[(int)b.Transform].World, positionB);
                        maximum = Math.Max(maximum, MaxDiff(pointA, pointB));
                        checkedVertices++;
                    }
                    else
                    {
                        for (var channel = 0; channel < influencesA.Count; channel++)
                        {
                            if (influencesA[channel].Weight != influencesB[channel].Weight) return false;
                            if (left.Graph[influencesA[channel].Scene].Hash != right.Graph[influencesB[channel].Scene].Hash) return false;
                            if (influencesA[channel].Internal is not int leftIndex || influencesB[channel].Internal is not int rightIndex)
                                return false;
                            var pointA = IasAffine.Point(left.Bones[a.Lod].Matrices[leftIndex], positionA);
                            var pointB = IasAffine.Point(right.Bones[b.Lod].Matrices[rightIndex], positionB);
                            maximum = Math.Max(maximum, MaxDiff(pointA, pointB));
                            checkedVertices++;
                        }
                    }
                }
            }
        }
        return checkedVertices > 0 && maximum < 2e-5;
    }

    private static bool NearPose(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (Math.Abs(left[index] - right[index]) >= 2e-5) return false;
        return true;
    }

    private static AssembledRoutes AssembleRoutes(IasCompanionData data, IasRouting routing,
        IReadOnlyDictionary<string, IasUnit> units, IReadOnlyDictionary<string, IasGeometryStats> geometry,
        IReadOnlyDictionary<string, IasProfileTable> profiles, IReadOnlyDictionary<string, string> nativeTypes,
        LegacyResult legacy)
    {
        var hidden = geometry.Where(pair => pair.Value.AnimatedTriangles == 0).Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        var usable = units.Keys.Where(uid => profiles.ContainsKey(uid) && !hidden.Contains(uid))
            .ToHashSet(StringComparer.Ordinal);
        var baseUnits = routing.Initial.Where(pair => !routing.Replaced.Contains(pair.Key) && usable.Contains(pair.Value))
            .Select(pair => pair.Value).Order(StringComparer.Ordinal).ToList();
        var parts = routing.Parts.Select(part => new PartState(part.Id, part.Name, part.Selected)).ToList();
        for (var index = 0; index < parts.Count; index++)
        {
            var source = routing.Parts[index];
            foreach (var option in source.Options)
                parts[index].Options.Add(new OptionState(option.Id, option.Name,
                    option.Units.Where(usable.Contains).Order(StringComparer.Ordinal)));
        }
        BodyRoutesState? bodyRoutes = null;
        var bodies = routing.Domains.Where(pair => pair.Value == "body").Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (bodies.Count > 0)
        {
            foreach (var native in bodies)
                IasBinary.Require(nativeTypes.TryGetValue(native, out var type) && type is "Slim" or "Stocky" or "Any",
                    "native body routing metadata is unavailable");
            var memberships = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["Slim"] = bodies.Where(native => nativeTypes[native] is "Slim" or "Any").ToHashSet(StringComparer.Ordinal),
                ["Stocky"] = bodies.Where(native => nativeTypes[native] is "Stocky" or "Any").ToHashSet(StringComparer.Ordinal)
            };
            List<string> Active(IReadOnlyDictionary<string, string> routes, string body)
                => routes.Where(pair => usable.Contains(pair.Value)
                                        && (routing.Domains[pair.Key] == "helmet" || memberships[body].Contains(pair.Key)))
                    .Select(pair => pair.Value).Order(StringComparer.Ordinal).ToList();
            var variants = new Dictionary<string, BodyVariantState>(StringComparer.Ordinal);
            foreach (var body in new[] { "Slim", "Stocky" })
            {
                IasBinary.Require(memberships.ContainsKey(body), "IAS body recipe is incomplete");
                variants[body] = new BodyVariantState(
                    Active(routing.Initial.Where(pair => !routing.Replaced.Contains(pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), body),
                    routing.Parts.Select(part => new BodyPartState(part.Options.Select(option =>
                        new BodyOptionState(Active(option.Routes, body))).ToList())).ToList());
            }
            var represented = variants.Values.SelectMany(variant => variant.Base)
                .Concat(variants.Values.SelectMany(variant => variant.Parts)
                    .SelectMany(part => part.Options).SelectMany(option => option.Units))
                .ToHashSet(StringComparer.Ordinal);
            IasBinary.Require(represented.SetEquals(usable), "unrouted authored body membership");
            bodyRoutes = new BodyRoutesState(variants);
        }
        var selected = new HashSet<string>(baseUnits, StringComparer.Ordinal);
        foreach (var option in parts.SelectMany(part => part.Options)) selected.UnionWith(option.Units);
        IasBinary.Require(selected.SetEquals(usable) && selected.Count > 0, "unrouted/empty active model");
        var heads = selected.Where(uid => routing.PublishedDomains.GetValueOrDefault(uid) == "helmet")
            .Order(StringComparer.Ordinal).ToList();
        var contentDomains = selected.Select(uid => routing.PublishedDomains.GetValueOrDefault(uid) == "helmet"
                ? "HELMET" : "BODY")
            .Distinct().Order(StringComparer.Ordinal).ToList();
        return new AssembledRoutes(baseUnits, parts, selected, heads, contentDomains, bodyRoutes, legacy.PartsMap);
    }

    private static Dictionary<string, IasBinding> BuildBindings(IasCompanionData data,
        IReadOnlyDictionary<string, IasProfileTable> profiles)
    {
        var bindings = new Dictionary<string, IasBinding>(StringComparer.Ordinal);
        foreach (var shared in data.Shared)
        foreach (var (uid, consumer) in shared.Consumers)
            bindings[uid] = new IasBinding(shared.Canonical, consumer.Mapping, consumer.Basis,
                consumer.Slot, consumer.Native, consumer.Choice);
        foreach (var group in data.Groups.Where(group => !group.Virtual))
            bindings[group.Id] = new IasBinding(group.Id,
                Enumerable.Range(1, profiles[group.Id].Nodes.Count).ToArray(), null, null, null, null);
        return bindings;
    }

    private sealed record AssembledRoutes(
        List<string> Base, List<PartState> Parts, HashSet<string> Selected, List<string> HeadUnits,
        List<string> ContentDomains, BodyRoutesState? BodyRoutes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LegacyParts);

    private sealed record LegacyResult(List<string> Base, List<PartState> Parts, HashSet<string> Selected,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> PartsMap);
}
