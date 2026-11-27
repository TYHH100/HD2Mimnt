using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasNativeMorphProof(
    bool Generated,
    string Status,
    IReadOnlyList<string> SourcePrograms,
    IReadOnlyList<string> Controls,
    IReadOnlyList<string> PrivateMaterials,
    bool GameVerified = false);

public sealed record IasNativeMorphResult(
    IasCompiledModel Compiled,
    IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> Materials,
    IasNativeMorphProof Proof);

/// <summary>HD2Morph1 到 native_vertex_morph_v1 的原子转换；输入模型与来源资源不被修改。</summary>
public static class IasNativeMorph
{
    private const double NormalNoise = 2e-7;
    private static readonly (uint Kind, uint Format, uint Index, uint Offset, uint Size)[] Layout56 =
        [(5, 4, 0, 0, 4), (0, 2, 0, 4, 12), (1, 30, 0, 16, 4), (4, 1, 0, 20, 8),
         (4, 1, 1, 28, 8), (4, 1, 2, 36, 8), (7, 35, 0, 44, 8), (6, 28, 0, 52, 4)];

    public static IasNativeMorphResult Apply(IasCompiledModel compiled, IReadOnlyList<IasMorphSourceProgram> programs,
        IReadOnlyDictionary<string, IasUnit> sourceUnits,
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> materialResources,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (programs.Count == 0)
            return new(compiled, new Dictionary<(ulong, ulong), IasResource>(), new(false, "ordinary_no_morph_programs", [], [], []));
        token.ThrowIfCancellationRequested();
        var catalogRoot = Clone(compiled.Catalog);
        var catalogModels = (IasValue.Array)Get(catalogRoot, "models");
        IasBinary.Require(catalogModels.Items.Count == 1, "Native morph conversion requires exactly one model");
        var model = (IasValue.Object)catalogModels.Items[0];
        IasBinary.Require(!model.Entries.Any(entry => entry.Key == "morph"), "Compile the ordinary model with the old procedural binder disabled first");
        var modelId = Text(model, "id");
        var displayUnits = TextDictionary(model, "display_units");
        var selected = displayUnits.Keys.ToHashSet(StringComparer.Ordinal);
        var (canonical, bindings) = Collect(programs, sourceUnits, selected, modelId);

        var plans = new List<MorphPlan>();
        var privateIds = new Dictionary<ulong, ulong>();
        var namespaceName = "hd2cl/native-morph-v3/" + modelId;
        foreach (var (uid, byMesh) in bindings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("核验原生形态绑定 " + uid);
            IasBinary.Require(sourceUnits.TryGetValue(uid, out var unit), "Selected morph source Unit is unavailable: " + uid);
            ValidateBindings(uid, unit, byMesh);
            var active = byMesh.Values.SelectMany(item => item.Deltas)
                .Where(pair => pair.Value.Values.Any(Meaningful))
                .Select(pair => pair.Key).Distinct()
                .OrderBy(id => canonical[id].Name, IasCodePointComparer.Instance).ThenBy(id => id, StringComparer.Ordinal).ToList();
            if (active.Count == 0) continue;
            IasBinary.Require(active.Count <= 6, uid + ": native profile supports at most six meaningful semantic controls");
            var used = byMesh.Keys.SelectMany(index => unit.Meshes[index].Sections)
                .Where(section => section.Faces.Count > 0 && section.Material is not null)
                .Select(section => section.Material!.Value).ToHashSet();
            IasBinary.Require(used.Count > 0, uid + ": morph profile has no original rendered Material consumers");
            foreach (var material in used)
            {
                IasBinary.Require(materialResources.ContainsKey((IasResourceTypes.Material, material)),
                    uid + ": missing original Material " + material.ToString("x16"));
                privateIds[material] = IasHash.Murmur64(namespaceName + "/material/" + material.ToString("x16"));
            }
            IasBinary.Require(displayUnits.ContainsKey(uid), "morph source Unit has no compiled display Unit");
            plans.Add(new MorphPlan(uid, unit, byMesh, active, used, displayUnits[uid]));
        }
        var usedIds = materialResources.Keys.Where(key => key.Type == IasResourceTypes.Material).Select(key => key.Item2)
            .Concat(compiled.MaterialIds).Concat(compiled.Owned.Keys.Where(key => key.Type == IasResourceTypes.Material).Select(key => key.Item2))
            .ToHashSet();
        IasBinary.Require(privateIds.Values.All(id => id != 0) && privateIds.Values.Distinct().Count() == privateIds.Count
                          && !privateIds.Values.Any(usedIds.Contains), "Private native Material identity collision");
        var sourceProof = programs.Select(program => program.Name + "@" + program.Sha256).Order(StringComparer.Ordinal).ToList();
        if (plans.Count == 0)
            return new(compiled, new Dictionary<(ulong, ulong), IasResource>(),
                new(false, "ordinary_no_selected_meaningful_morphs", sourceProof, [], []));

        var owned = new Dictionary<(ulong Type, ulong Id), IasResource>(compiled.Owned, FriendlyEqualityComparer.Instance);
        var materials = new Dictionary<(ulong Type, ulong Id), IasResource>(FriendlyEqualityComparer.Instance);
        using var validator = new IasNativeMorphShader.DeviceValidator();
        var shaderCache = new Dictionary<(string Sha, int[] Offsets), ShaderInjection>();
        foreach (var (originalId, privateId) in privateIds.OrderBy(pair => pair.Key))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("生成配套原生形态材质 " + originalId.ToString("x16"));
            try
            {
                materials[(IasResourceTypes.Material, privateId)] = IasNativeMorphMaterial.Convert(
                    materialResources[(IasResourceTypes.Material, originalId)], originalId, privateId,
                    namespaceName + "/material/" + originalId.ToString("x16"), validator, shaderCache, token);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
            {
                throw new InvalidDataException("Material " + originalId.ToString("x16") + ": " + error.Message, error);
            }
        }

        IasNativeMorphMaterial.ValidateMaterials(materials);
        var profiles = new SortedDictionary<string, IasValue>(StringComparer.Ordinal);
        foreach (var plan in plans.OrderBy(plan => plan.Uid, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("生成保留原始网格的形态流 " + plan.Uid);
            var key = (IasResourceTypes.Unit, ulong.Parse(plan.Display, NumberStyles.HexNumber));
            IasBinary.Require(owned.TryGetValue(key, out var target), "compiled TP Unit is not owned");
            var materialMap = plan.OriginalMaterials.ToDictionary(id => id, id => privateIds[id]);
            try
            {
                owned[key] = ExtendUnit(plan.Uid, plan.Unit, target, plan.Bindings, plan.Active, materialMap, token);
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or ArgumentException)
            {
                throw new InvalidDataException("Unit " + plan.Uid + ": " + error.Message, error);
            }
            profiles[plan.Uid] = IasValue.Obj(
                ("materials", IasValue.Of(materialMap.Values.Order().Select(id => IasValue.Of(id.ToString("x16"))).ToList())),
                ("controls", IasValue.Obj(plan.Active.Select((id, slot) => (id, IasValue.Of(ControlName(slot)))).ToArray())));
        }
        var activeIds = profiles.Values.SelectMany(profile => Dictionary(profile, "controls").Keys).Distinct()
            .OrderBy(id => canonical[id].Name, IasCodePointComparer.Instance).ThenBy(id => id, StringComparer.Ordinal).ToList();
        var controls = activeIds.Select(id => IasValue.Obj(
            ("id", IasValue.Of(id)), ("name", IasValue.Of(canonical[id].Name)),
            ("min", IasValue.Of(canonical[id].Min)), ("max", IasValue.Of(canonical[id].Max)),
            ("default", IasValue.Of(0.0)))).ToList();
        var blink = controls.Where(control => Text(control, "name").ToLowerInvariant() is "eye_close" or "blink" or "eyes_closed" or "まばたき")
            .Select(control => Text(control, "id")).ToList();
        var morph = IasValue.Obj(
            ("version", IasValue.Of(3L)), ("backend", IasValue.Of("native_vertex_morph_v1")),
            ("native_face_preserved", IasValue.Of(true)), ("channels", IasValue.Of(controls)),
            ("blink", blink.Count == 1 ? IasValue.Of(blink[0]) : IasValue.Of(false)),
            ("profiles", IasValue.Obj(profiles.Select(pair => (pair.Key, pair.Value)).ToArray())),
            ("automatic_default", IasValue.Of(true)), ("idle_automatic_default", IasValue.Of(true)),
            ("normal_representation", IasValue.Of("sdk_normalized_linear_target_oct15_v1")));
        var convertedModel = (IasValue.Object)With(model, "morph", morph);
        var modelList = catalogModels.Items.ToList();
        modelList[0] = convertedModel;
        var catalog = With(catalogRoot, "models", IasValue.Of(modelList));
        var firstPerson = SeparateFirstPerson(convertedModel, profiles.Keys.ToHashSet(StringComparer.Ordinal), owned);
        _ = firstPerson;
        var registration = With(compiled.Registration, "protocol", IasValue.Of(2L));
        registration = With(registration, "morph_backend", IasValue.Of("native_vertex_morph_v1"));
        registration = With(registration, "minimum_test_version", IasValue.Of("1.4.18"));
        var materialIds = compiled.MaterialIds.Concat(privateIds.Values).Distinct().Order().ToList();
        registration = With(registration, "materials", IasValue.Of(materialIds.Select(id => IasValue.Of(id.ToString("x16"))).ToList()));
        var proof = new IasNativeMorphProof(true, "native_v3_converted_offline", sourceProof,
            activeIds.Select(id => canonical[id].Name).ToList(), privateIds.Values.Order().Select(id => id.ToString("x16")).ToList());
        return new IasNativeMorphResult(new IasCompiledModel(compiled.Name, owned, catalog, registration,
            compiled.Domains, materialIds, compiled.VisibleMaterialIds, compiled.Geometry, proof), materials, proof);
    }

    private sealed record CanonicalChannel(string Id, string Name, float Min, float Max,
        HashSet<string> RawIds, HashSet<string> SourceObjects);

    private sealed class Binding
    {
        public required IasMorphMesh Header { get; init; }
        public Dictionary<string, Dictionary<int, float[]>> Deltas { get; } = new(StringComparer.Ordinal);
    }

    private sealed record MorphPlan(string Uid, IasUnit Unit, Dictionary<int, Binding> Bindings,
        List<string> Active, HashSet<ulong> OriginalMaterials, string Display);

    private static (Dictionary<string, CanonicalChannel>, Dictionary<string, Dictionary<int, Binding>>) Collect(
        IReadOnlyList<IasMorphSourceProgram> programs, IReadOnlyDictionary<string, IasUnit> units,
        HashSet<string> selected, string modelId)
    {
        var groups = new Dictionary<(string Name, float Min, float Max, float Baseline), CanonicalChannel>();
        var raw = new Dictionary<string, IasMorphChannel>();
        var bindings = new Dictionary<string, Dictionary<int, Binding>>(StringComparer.Ordinal);
        foreach (var program in programs)
        {
            var localIds = new Dictionary<string, string>();
            foreach (var channel in program.Document.Channels)
            {
                IasBinary.Require(!channel.Muted && !channel.HasDriver && channel.RelativeKey == "Basis"
                                  && channel.Default == channel.Baseline && channel.Baseline == 0 && channel.NormalSampleWeight == 1,
                    "Only active, undriven Basis keys with zero default/baseline and normal sample 1 are supported: " + channel.Name);
                var min = (float)channel.Min;
                var max = (float)channel.Max;
                var identity = (channel.Name, min, max, 0.0f);
                if (raw.TryGetValue(channel.Id, out var previous)) IasBinary.Require(previous == channel, "Conflicting raw channel ID: " + channel.Id);
                raw[channel.Id] = channel;
                if (!groups.TryGetValue(identity, out var group))
                {
                    var encoded = IasLua.JsonBytes(IasValue.Of(new IasValue[]
                    {
                        IasValue.Of(modelId), IasValue.Of(channel.Name), IasValue.Of(FloatHex(min)),
                        IasValue.Of(FloatHex(max)), IasValue.Of("0")
                    }));
                    group = new CanonicalChannel(IasHash.Sha256Hex(encoded)[..32], channel.Name, min, max, [], []);
                    groups.Add(identity, group);
                }
                group.RawIds.Add(channel.Id);
                group.SourceObjects.Add(channel.SourceObject);
                localIds[channel.Id] = group.Id;
            }
            foreach (var mesh in program.Document.Meshes)
            {
                if (!selected.Contains(mesh.UnitId)) continue;
                IasBinary.Require(units.ContainsKey(mesh.UnitId), "Selected morph source Unit is unavailable: " + mesh.UnitId);
                if (!bindings.TryGetValue(mesh.UnitId, out var byMesh)) bindings[mesh.UnitId] = byMesh = new Dictionary<int, Binding>();
                var meshIndex = checked((int)mesh.MeshIndex);
                if (!byMesh.TryGetValue(meshIndex, out var target))
                {
                    target = new Binding { Header = mesh };
                    byMesh.Add(meshIndex, target);
                }
                IasBinary.Require(HeaderEquals(target.Header, mesh), mesh.UnitId + ": morph programs disagree on mesh binding " + mesh.MeshIndex);
                foreach (var delta in mesh.Deltas)
                {
                    var gid = localIds[delta.ChannelId];
                    target.Deltas.TryAdd(gid, new());
                    var records = target.Deltas[gid];
                    foreach (var record in delta.Records)
                        records[record.Vertex] = MergeRecord(records.GetValueOrDefault(record.Vertex), record.Values,
                            mesh.UnitId + "/mesh" + mesh.MeshIndex + "/" + delta.ChannelId + "/vertex" + record.Vertex);
                }
            }
        }
        var canonical = groups.Values.ToDictionary(group => group.Id, StringComparer.Ordinal);
        IasBinary.Require(canonical.Count == groups.Count, "Canonical morph identity collision");
        return (canonical, bindings);
    }

    private static bool HeaderEquals(IasMorphMesh left, IasMorphMesh right)
        => left.UnitId == right.UnitId && left.MeshIndex == right.MeshIndex && left.LodIndex == right.LodIndex
           && left.StreamIndex == right.StreamIndex && left.VertexOffset == right.VertexOffset && left.UnitVersion == right.UnitVersion
           && left.VertexCount == right.VertexCount && left.StreamVertexCount == right.StreamVertexCount && left.Stride == right.Stride
           && left.VertexBufferOffset == right.VertexBufferOffset && left.VertexBufferSize == right.VertexBufferSize
           && left.StreamSha256 == right.StreamSha256 && left.IndexSha256 == right.IndexSha256 && left.UnitSha256 == right.UnitSha256
           && left.Components.SequenceEqual(right.Components) && left.BoundsMin.SequenceEqual(right.BoundsMin)
           && left.BoundsMax.SequenceEqual(right.BoundsMax) && left.IndexStride == right.IndexStride
           && left.Sections.SequenceEqual(right.Sections);

    private static float[] MergeRecord(float[]? previous, float[] current, string context)
    {
        if (previous is null || previous.SequenceEqual(current)) return current;
        if (!Meaningful(previous)) return current;
        if (!Meaningful(current)) return previous;
        for (var index = 0; index < 6; index++)
            IasBinary.Require(Math.Abs(previous[index] - current[index]) <= (index < 3 ? 1e-10 : NormalNoise),
                context + ": same-name source keys have conflicting nonzero deltas");
        return previous;
    }

    private static bool Meaningful(float[] values)
        => values[..3].Any(value => value != 0) || values.Skip(3).Any(value => Math.Abs(value) > NormalNoise);

    private static void ValidateBindings(string uid, IasUnit unit, Dictionary<int, Binding> bindings)
    {
        var fingerprint = IasHash.ResourceFingerprint(unit.Resource);
        var expected = unit.Meshes.Where(mesh => mesh.Lod >= 0 && !mesh.Culling).Select(mesh => mesh.Index).ToHashSet();
        IasBinary.Require(bindings.Keys.ToHashSet().SetEquals(expected), uid + ": morph must bind every actual render mesh/LOD, with no extra mesh");
        foreach (var (index, item) in bindings.OrderBy(pair => pair.Key))
        {
            var header = item.Header;
            var mesh = unit.Meshes[index];
            var stream = unit.Streams[(int)mesh.Stream];
            var prefix = uid + "/mesh" + index + ": ";
            IasBinary.Require(header.UnitSha256 == fingerprint, prefix + "source Unit fingerprint differs");
            IasBinary.Require(header.UnitVersion == unit.Version && header.LodIndex == mesh.Lod && header.StreamIndex == mesh.Stream
                              && header.VertexCount == mesh.VertexCount && header.StreamVertexCount == stream.Vertices
                              && header.Stride == stream.Stride && header.VertexBufferOffset == (ulong)stream.VertexBase
                              && header.VertexBufferSize == (ulong)stream.VertexBytes && header.IndexStride == stream.IndexStride,
                prefix + "stream/mesh descriptor differs");
            IasBinary.Require(stream.Stride == 56 && stream.VertexBytes >= stream.Vertices * 56,
                prefix + "only the reviewed native stride56 layout is supported");
            var actual = stream.Components.Select(component => (component.Kind, component.Format, component.Index, (uint)component.Offset, 0u)).ToArray();
            IasBinary.Require(actual.Length == Layout56.Length && actual.Zip(Layout56).All(pair =>
                pair.First.Item1 == pair.Second.Kind && pair.First.Item2 == pair.Second.Format
                && pair.First.Item3 == pair.Second.Index && pair.First.Item4 == pair.Second.Offset),
                prefix + "unsupported vertex component layout");
            IasBinary.Require(header.Components.Select(component => (component.Type, component.Format, component.Index, component.Offset, component.Size))
                .SequenceEqual(Layout56), prefix + "sidecar vertex components differ from source");
            var relative = mesh.VertexStart - stream.VertexBase;
            IasBinary.Require(relative >= 0 && relative % stream.Stride == 0 && (uint)(relative / stream.Stride) == header.VertexOffset,
                prefix + "vertex span offset differs");
            IasBinary.Require(IasHash.Sha256Hex(unit.Resource.Gpu.Span.Slice(stream.VertexBase, stream.VertexBytes)) == header.StreamSha256,
                prefix + "vertex stream SHA256 differs");
            IasBinary.Require(header.Sections.Count == mesh.Sections.Count, prefix + "section count differs");
            using var indices = IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (var section = 0; section < mesh.Sections.Count; section++)
            {
                var original = mesh.Sections[section];
                var declared = header.Sections[section];
                IasBinary.Require(original.IndexOffset == declared.IndexByteOffset && original.Indices == declared.IndexCount,
                    prefix + "section index range differs");
                indices.AppendData(unit.Resource.Gpu.Span.Slice(original.IndexOffset, checked((int)original.Indices * stream.IndexStride)));
            }
            IasBinary.Require(Convert.ToHexString(indices.GetHashAndReset()).ToLowerInvariant() == header.IndexSha256,
                prefix + "section indices SHA256 differs");
        }
    }

    private static IasResource ExtendUnit(string uid, IasUnit source, IasResource target,
        Dictionary<int, Binding> bindings, List<string> channelIds, Dictionary<ulong, ulong> materialMap,
        CancellationToken token)
    {
        IasBinary.Require(target.Gpu.Length == source.Resource.Gpu.Length && target.Stream.Length == source.Resource.Stream.Length
                          && target.Gpu.Span.SequenceEqual(source.Resource.Gpu.Span) && target.Stream.Span.SequenceEqual(source.Resource.Stream.Span),
            uid + ": compiled TP is not the ordinary unmasked source geometry");
        var before = target.Toc;
        var original = source.Resource;
        IasBinary.Require(before.Length == original.Toc.Length && before.AsSpan(0, 8).SequenceEqual(original.Toc.AsSpan(0, 8))
                          && before.AsSpan(16).SequenceEqual(original.Toc.AsSpan(16))
                          && (before.AsSpan(8, 8).SequenceEqual(original.Toc.AsSpan(8, 8)) || source.Refs[1] != 0),
            uid + ": compiled TP contains unsupported source TOC edits");
        var toc = (byte[])before.Clone();
        var targetGpu = new byte[checked((int)target.Gpu.Length)];
        target.Gpu.CopyTo(0, targetGpu);
        using var gpuStream = new MemoryStream(targetGpu);
        var streamTable = checked((int)IasBinary.ReadUInt32(toc, 92));
        var descriptors = new List<int>();
        for (var index = 0; index < IasBinary.ReadUInt32(toc, streamTable); index++)
            descriptors.Add(streamTable + (int)IasBinary.ReadUInt32(toc, streamTable + 4 + index * 4));
        var pending = new Dictionary<uint, StreamState>();
        foreach (var (index, binding) in bindings.OrderBy(pair => pair.Key))
        {
            token.ThrowIfCancellationRequested();
            var mesh = source.Meshes[index];
            var stream = source.Streams[(int)mesh.Stream];
            if (!pending.TryGetValue(mesh.Stream, out var state))
            {
                state = new StreamState(stream.Vertices * 104);
                pending.Add(mesh.Stream, state);
            }
            var first = binding.Header.VertexOffset;
            var count = binding.Header.VertexCount;
            var local = new byte[checked((int)count) * 104];
            for (var slot = 0; slot < channelIds.Count; slot++)
            {
                if (!binding.Deltas.TryGetValue(channelIds[slot], out var deltas)) continue;
                foreach (var (vertex, values) in deltas)
                {
                    for (var axis = 0; axis < 3; axis++)
                        BinaryPrimitives.WriteSingleLittleEndian(local.AsSpan(vertex * 104 + slot * 12 + axis * 4, 4), values[axis]);
                    var bits = IasBinary.ReadUInt32(original.Gpu.Span, stream.VertexBase + (int)(first + (uint)vertex) * 56 + 16);
                    var (packed, _) = TargetNormal(bits, values[3..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(vertex * 104 + 72 + slot * 4, 4), packed);
                }
            }
            for (var vertex = 0; vertex < (int)count; vertex++)
            {
                var at = (int)(first + (uint)vertex) * 104;
                var value = local.AsSpan(vertex * 104, 104);
                if (state.Occupied.Contains((int)(first + (uint)vertex)))
                    IasBinary.Require(state.Payload.AsSpan(at, 104).SequenceEqual(value), uid + ": overlapping mesh payloads conflict");
                value.CopyTo(state.Payload.AsSpan(at));
                state.Occupied.Add((int)(first + (uint)vertex));
            }
        }
        foreach (var (streamIndex, state) in pending.OrderBy(pair => pair.Key))
        {
            var stream = source.Streams[(int)streamIndex];
            var merged = new byte[stream.Vertices * 160];
            for (var vertex = 0; vertex < stream.Vertices; vertex++)
            {
                var src = stream.VertexBase + vertex * 56;
                var dst = vertex * 160;
                var payload = vertex * 104;
                original.Gpu.Span.Slice(src, 20).CopyTo(merged.AsSpan(dst, 20));
                for (var uv = 0; uv < 3; uv++)
                {
                    original.Gpu.Span.Slice(src + 20 + uv * 8, 8).CopyTo(merged.AsSpan(dst + 20 + uv * 16, 8));
                    state.Payload.AsSpan(payload + uv * 8, 8).CopyTo(merged.AsSpan(dst + 28 + uv * 16, 8));
                }
                original.Gpu.Span.Slice(src + 44, 12).CopyTo(merged.AsSpan(dst + 68, 12));
                state.Payload.AsSpan(payload + 24, 80).CopyTo(merged.AsSpan(dst + 80, 80));
            }
            while (gpuStream.Length % 64 != 0) gpuStream.WriteByte(0);
            var offset = gpuStream.Length;
            gpuStream.Write(merged);
            var descriptor = descriptors[(int)streamIndex];
            for (var component = 0; component < stream.Components.Count; component++)
                if (stream.Components[component].Kind == 4)
                    BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(descriptor + 8 + component * 20 + 4, 4), 3);
            for (var component = 0; component < 5; component++)
            {
                var at = descriptor + 8 + (8 + component) * 20;
                BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(at, 4), 4);
                BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(at + 4, 4), 3);
                BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(at + 8, 4), 3 + (uint)component);
                BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(at + 12, 8), 0);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(descriptor + 328, 8), 13);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(descriptor + 356, 4), 160);
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(descriptor + 416, 4), checked((uint)offset));
            BinaryPrimitives.WriteUInt32LittleEndian(toc.AsSpan(descriptor + 420, 4), (uint)merged.Length);
        }
        var table = checked((int)IasBinary.ReadUInt32(toc, 112));
        var materialCount = checked((int)IasBinary.ReadUInt32(toc, table));
        for (var index = 0; index < materialCount; index++)
        {
            var at = table + 4 + materialCount * 4 + index * 8;
            var originalId = IasBinary.ReadUInt64(toc, at);
            if (materialMap.TryGetValue(originalId, out var mapped)) BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(at, 8), mapped);
        }
        var gpu = gpuStream.ToArray();
        IasBinary.Require(gpu.AsSpan(0, checked((int)target.Gpu.Length)).SequenceEqual(target.Gpu.Span), uid + ": original GPU prefix changed");
        return target with { Toc = toc, Gpu = IasPayload.From(gpu) };
    }

    private sealed class StreamState(int size)
    {
        public byte[] Payload { get; } = new byte[size];
        public HashSet<int> Occupied { get; } = [];
    }

    private static (uint Packed, double Error) TargetNormal(uint bits, float[] delta)
    {
        if (delta.Max(value => Math.Abs(value)) < 1e-12) return (0, 0);
        IasBinary.Require(delta.Max(value => Math.Abs(value)) <= 2.000001, "Normalized source normal delta exceeds [-2, 2]");
        var normal = DecodeNormal(bits);
        var target = normal.Zip(delta, (a, b) => a + b).ToArray();
        var length = Math.Sqrt(target.Sum(value => value * value));
        IasBinary.Require(length > 1e-12, "Source target normal has zero length");
        target = target.Select(value => value / length).ToArray();
        var norm = target.Sum(Math.Abs);
        var x = target[0] / norm;
        var y = target[1] / norm;
        if (target[2] < 0)
        {
            x = (1 - Math.Abs(y)) * (x >= 0 ? 1 : -1);
            y = (1 - Math.Abs(x)) * (y >= 0 ? 1 : -1);
        }
        var qx = (int)Math.Round((x * 0.5 + 0.5) * 32767, MidpointRounding.ToEven);
        var qy = (int)Math.Round((y * 0.5 + 0.5) * 32767, MidpointRounding.ToEven);
        qx = Math.Clamp(qx, 0, 32767);
        qy = Math.Clamp(qy, 0, 32767);
        var error = Math.Max(Math.Abs(qx * 2.0 / 32767 - 1 - x), Math.Abs(qy * 2.0 / 32767 - 1 - y));
        return ((uint)(qx | (qy << 15)) + 1, error);
    }

    private static double[] DecodeNormal(uint bits)
    {
        var x = (bits & 1023) * 2.0 / 1023 - 1;
        var y = ((bits >> 10) & 1023) * 2.0 / 1023 - 1;
        var z = 1 - Math.Abs(x) - Math.Abs(y);
        var t = Math.Max(-z, 0);
        x += x >= 0 ? -t : t;
        y += y >= 0 ? -t : t;
        var length = Math.Sqrt(x * x + y * y + z * z);
        return [x / length, y / length, z / length];
    }

    private static string FloatHex(float value)
    {
        if (value == 0) return "0";
        return Convert.ToHexString(BitConverter.GetBytes(value)).ToLowerInvariant();
    }

    private static string ControlName(int slot) => slot switch
    {
        0 => "glitch_amount", 1 => "glitch_speed", 2 => "glitch_gridsize",
        3 => "glitch_color_power", 4 => "glitch_center_offset", _ => "self_light"
    };

    private static IReadOnlyDictionary<string, string> SeparateFirstPerson(IasValue.Object model,
        HashSet<string> morphProfiles, IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> owned)
    {
        var views = Dictionary(model, "views");
        foreach (var (role, value) in views.OrderBy(pair => pair.Key, IasCodePointComparer.Instance))
        {
            var view = (IasValue.Object)value;
            IasBinary.Require(!view.Entries.Any(entry => entry.Key == "morph"), role + ": first-person view must not declare native morphs");
            var key = role == "arms" ? "first_person" : "display_units";
            var mapping = TextDictionary(view, key);
            var profiles = Dictionary(view, "profiles");
            var aliases = profiles.Keys.Where(morphProfiles.Contains).ToDictionary(uid => uid, uid => mapping[uid], StringComparer.Ordinal);
            IasBinary.Require(aliases.Values.Distinct().Count() == aliases.Count
                              && !aliases.Values.Any(id => profiles.ContainsKey(id) || morphProfiles.Contains(id)), role + ": first-person profile alias collision");
            foreach (var alias in aliases.Values)
                IasBinary.Require(owned.ContainsKey((IasResourceTypes.Unit, ulong.Parse(alias, NumberStyles.HexNumber))), role + ": first-person alias has no owned ordinary Unit");
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layoutValue in Layouts(view))
            {
                var layout = (IasValue.Object)layoutValue;
                var baseUnits = Strings(layout, "base");
                referenced.UnionWith(baseUnits);
                var converted = With(layout, "base", IasValue.Of(baseUnits.Select(id => IasValue.Of(aliases.GetValueOrDefault(id, id))).ToList()));
                if (layout.Entries.Any(entry => entry.Key == "parts"))
                {
                    var parts = (IasValue.Array)Get(layout, "parts");
                    var newParts = parts.Items.Select(partValue =>
                    {
                        var part = (IasValue.Object)partValue;
                        if (!part.Entries.Any(entry => entry.Key == "options")) return part;
                        var options = (IasValue.Array)Get(part, "options");
                        return With(part, "options", IasValue.Of(options.Items.Select(optionValue =>
                        {
                            var option = (IasValue.Object)optionValue;
                            var units = Strings(option, "units");
                            referenced.UnionWith(units);
                            return With(option, "units", IasValue.Of(units.Select(id => IasValue.Of(aliases.GetValueOrDefault(id, id))).ToList()));
                        }).ToList()));
                    }).ToList();
                    converted = With(converted, "parts", IasValue.Of(newParts));
                }
                if (ReferenceEquals(layout, view)) ReplaceView(view, key, converted);
                else ReplaceBodyRoute(view, layoutValue, converted);
            }
            ReplaceView(view, "profiles", RemapDictionary(profiles, aliases));
            ReplaceView(view, key, RemapDictionary(mapping.ToDictionary(pair => pair.Key, pair => IasValue.Of(pair.Value), StringComparer.Ordinal), aliases));
            if (view.Entries.Any(entry => entry.Key == "display_drive"))
            {
                var drive = (IasValue.Object)Get(view, "display_drive");
                if (drive.Entries.Any(entry => entry.Key == "resources"))
                {
                    var resources = Dictionary(drive, "resources");
                    var converted = new Dictionary<string, IasValue>(resources, StringComparer.Ordinal);
                    foreach (var (uid, alias) in aliases)
                    {
                        var display = mapping[uid];
                        IasBinary.Require(resources.ContainsKey(display), role + ": first-person alias has no display-drive record");
                        converted[display] = With((IasValue.Object)resources[display], "profile", IasValue.Of(alias));
                    }
                    ReplaceView(view, "display_drive", With(drive, "resources", IasValue.Obj(converted.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
                        .Select(pair => (pair.Key, pair.Value)).ToArray())));
                }
            }
            IasBinary.Require(!referenced.Any(morphProfiles.Contains), role + ": first-person still references a TP morph profile");
        }
        return new Dictionary<string, string>();
    }

    private static IEnumerable<IasValue> Layouts(IasValue.Object view)
    {
        yield return view;
        if (!view.Entries.Any(entry => entry.Key == "ias_body_routes")) yield break;
        var routes = (IasValue.Object)Get(view, "ias_body_routes");
        if (!routes.Entries.Any(entry => entry.Key == "variants")) yield break;
        foreach (var variant in Dictionary(routes, "variants").Values) yield return variant;
    }

    private static void ReplaceView(IasValue.Object view, string key, IasValue value)
    {
        var entries = view.Entries.ToList();
        var index = entries.FindIndex(entry => entry.Key == key);
        if (index < 0) entries.Add(new(key, value));
        else entries[index] = new(key, value);
        view.ClearAndAdd(entries);
    }

    private static void ReplaceBodyRoute(IasValue.Object view, IasValue original, IasValue replacement)
    {
        var routes = (IasValue.Object)Get(view, "ias_body_routes");
        var variants = Dictionary(routes, "variants");
        var key = variants.First(pair => ReferenceEquals(pair.Value, original)).Key;
        ReplaceView(routes, "variants", RemapDictionary(variants, new Dictionary<string, string> { [key] = key }, _ => replacement));
        ReplaceView(view, "ias_body_routes", routes);
    }

    private static IasValue RemapDictionary(IReadOnlyDictionary<string, IasValue> source,
        IReadOnlyDictionary<string, string> aliases, Func<IasValue, IasValue>? convert = null)
        => IasValue.Obj(source.OrderBy(pair => pair.Key, IasCodePointComparer.Instance)
            .Select(pair => (aliases.GetValueOrDefault(pair.Key, pair.Key), convert?.Invoke(pair.Value) ?? pair.Value)).ToArray());

    private static IasValue Clone(IasValue value) => value switch
    {
        IasValue.Array array => IasValue.Of(array.Items.Select(Clone).ToList()),
        IasValue.Object obj => new IasValue.Object(obj.Entries.Select(entry => new KeyValuePair<string, IasValue>(entry.Key, Clone(entry.Value))).ToList()),
        _ => value
    };

    private static IasValue With(IasValue value, string key, IasValue replacement)
    {
        var entries = ((IasValue.Object)value).Entries.ToList();
        var index = entries.FindIndex(entry => entry.Key == key);
        if (index < 0) entries.Add(new(key, replacement));
        else entries[index] = new(key, replacement);
        return new IasValue.Object(entries);
    }

    private static IasValue Get(IasValue value, string key)
        => ((IasValue.Object)value).Entries.First(entry => entry.Key == key).Value;

    private static string Text(IasValue value, string key) => ((IasValue.Text)Get(value, key)).Value;

    private static Dictionary<string, IasValue> Dictionary(IasValue value, string key)
        => ((IasValue.Object)Get(value, key)).Entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static List<string> Strings(IasValue value, string key)
        => ((IasValue.Array)Get(value, key)).Items.Select(item => ((IasValue.Text)item).Value).ToList();

    private static Dictionary<string, string> TextDictionary(IasValue value, string key)
        => Dictionary(value, key).ToDictionary(pair => pair.Key, pair => ((IasValue.Text)pair.Value).Value, StringComparer.Ordinal);

    private sealed class FriendlyEqualityComparer : IEqualityComparer<(ulong Type, ulong Id)>
    {
        public static readonly FriendlyEqualityComparer Instance = new();
        public bool Equals((ulong Type, ulong Id) x, (ulong Type, ulong Id) y) => x.Type == y.Type && x.Id == y.Id;
        public int GetHashCode((ulong Type, ulong Id) obj) => HashCode.Combine(obj.Type, obj.Id);
    }
}

internal static class IasValueObjectExtensions
{
    public static void ClearAndAdd(this IasValue.Object value, IReadOnlyList<KeyValuePair<string, IasValue>> entries)
    {
        value.Entries = entries;
    }
}
