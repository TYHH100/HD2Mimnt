using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>原生形态私有材质：扩展 c_per_object、注入顶点 DXBC，并隔离技术标识。</summary>
public static class IasNativeMorphMaterial
{
    private static readonly string[] ControlNames =
        ["glitch_amount", "glitch_speed", "glitch_gridsize", "glitch_color_power", "glitch_center_offset", "self_light"];
    private static readonly uint[] ControlHashes = ControlNames.Select(IasHash.Murmur32).ToArray();

    public static IasResource Convert(IasResource source, ulong originalId, ulong privateId, string namespaceName,
        IasNativeMorphShader.DeviceValidator validator,
        Dictionary<(string Sha, int[] Offsets), ShaderInjection> shaderCache, CancellationToken token)
    {
        var (textures, values) = MaterialValues(source.Toc);
        foreach (var name in ControlNames)
            IasBinary.Require(values.TryGetValue(IasHash.Murmur32(name), out var value) && value.SequenceEqual([0.0]),
                "The six original scalar values must explicitly be zero");
        var original = Models(source.Gpu.Span);
        var (expanded, buffers) = PrivateObjectBuffers(source.Gpu.Span, namespaceName, values.Keys);
        var grouped = Models(expanded);
        for (var blockIndex = 0; blockIndex < grouped.Count; blockIndex++)
        {
            token.ThrowIfCancellationRequested();
            var configuration = buffers[blockIndex];
            for (var programIndex = 0; programIndex < grouped[blockIndex].Count; programIndex++)
            {
                var program = grouped[blockIndex][programIndex];
                var previous = original[blockIndex][programIndex];
                var stage = program.Stages[0];
                IasBinary.Require(stage.Code.Length > 0, "Native material has an empty vertex stage");
                var key = (IasHash.Sha256Hex(stage.Code), configuration.WeightOffsets);
                if (!shaderCache.TryGetValue(key, out var injected))
                {
                    injected = IasNativeMorphShader.Inject(stage.Code, configuration.WeightOffsets, validator);
                    shaderCache[key] = injected;
                }
                stage.Code = injected.Code;
                stage.Name = ContentId(stage.Code);
                var rows = new List<(uint Input, uint Size, uint Slot, uint Count)>();
                for (var offset = 0; offset < stage.Metadata[0].Length; offset += 16)
                    rows.Add((U32(stage.Metadata[0], offset), U32(stage.Metadata[0], offset + 4),
                        U32(stage.Metadata[0], offset + 8), U32(stage.Metadata[0], offset + 12)));
                if (injected.AddedObjectBufferBinding)
                    rows.Add(((uint)configuration.InputIndex, (uint)injected.ObjectBufferSize, (uint)injected.Slot, 1));
                else
                {
                    var matched = rows.Select((row, index) => (row, index))
                        .Where(item => item.row.Input == configuration.InputIndex && item.row.Slot == injected.Slot).ToList();
                    IasBinary.Require(matched.Count == 1, "Vertex object-buffer binding does not match CPU input");
                    var index = matched[0].index;
                    rows[index] = rows[index] with { Size = (uint)injected.ObjectBufferSize };
                }
                stage.Metadata[0] = rows.SelectMany(row => BitConverter.GetBytes(row.Input)
                    .Concat(BitConverter.GetBytes(row.Size)).Concat(BitConverter.GetBytes(row.Slot))
                    .Concat(BitConverter.GetBytes(row.Count))).ToArray();
                IasBinary.Require(program.States.AsSpan().SequenceEqual(previous.States)
                                  && ByteListsEqual(program.Attrs, previous.Attrs) && ByteListsEqual(program.Samplers, previous.Samplers)
                                  && program.Vertex.AsSpan().SequenceEqual(previous.Vertex)
                                  && program.Stages.Skip(1).Zip(previous.Stages.Skip(1)).All(pair =>
                                      pair.First.Name == pair.Second.Name && pair.First.Code.AsSpan().SequenceEqual(pair.Second.Code)
                                      && ByteListsEqual(pair.First.Metadata, pair.Second.Metadata)),
                    "Original native draw, texture state or non-vertex shader stage changed");
            }
        }
        var gpu = Serialize(source.Gpu.Span, grouped);
        gpu = BindMaterialId(gpu, privateId);
        gpu = IsolateTechniques(gpu, namespaceName);
        var toc = (byte[])source.Toc.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(toc.AsSpan(16, 8), (ulong)gpu.Length);
        var result = source with { Toc = toc, Gpu = IasPayload.From(gpu) };
        var (resultTextures, resultValues) = MaterialValues(result.Toc);
        IasBinary.Require(resultTextures.OrderBy(pair => pair.Key).SequenceEqual(textures.OrderBy(pair => pair.Key))
                          && resultValues.Count == values.Count
                          && resultValues.All(pair => values.TryGetValue(pair.Key, out var value) && value.SequenceEqual(pair.Value)),
            "Original texture bindings or TOC values changed");
        ValidateEntry(privateId, result);
        return result;
    }

    private static (Dictionary<uint, ulong> Textures, Dictionary<uint, double[]> Values) MaterialValues(byte[] data)
    {
        var textureCount = checked((int)U32(data, 64));
        var variableCount = checked((int)U32(data, 104));
        IasBinary.Require(textureCount <= 4096 && variableCount <= 100_000, "Unsupported native material or shader layout");
        var usages = new uint[textureCount];
        var textures = new ulong[textureCount];
        for (var index = 0; index < textureCount; index++)
        {
            usages[index] = U32(data, 136 + index * 4);
            textures[index] = U64(data, 136 + textureCount * 4 + index * 8);
        }
        var table = 136 + textureCount * 12;
        var baseOffset = checked(table + variableCount * 20);
        IasBinary.Require(baseOffset <= data.Length, "Native material variable table out of range");
        var values = new Dictionary<uint, double[]>();
        for (var index = 0; index < variableCount; index++)
        {
            var at = table + index * 20;
            var kind = U32(data, at);
            var count = U32(data, at + 4);
            var name = U32(data, at + 8);
            var offset = U32(data, at + 12);
            var stride = U32(data, at + 16);
            IasBinary.Require(kind is <= 3 && count == 0 && stride == 0, "Unsupported native material or shader layout");
            var length = (int)kind + 1;
            IasBinary.Require((long)baseOffset + offset + length * 4 <= data.Length, "Native material variable payload out of range");
            var row = new double[length];
            for (var component = 0; component < length; component++)
            {
                var position = checked(baseOffset + (int)offset + component * 4);
                row[component] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(position, 4));
            }
            values[name] = row;
        }
        return (usages.Zip(textures, (usage, texture) => (usage, texture)).ToDictionary(pair => pair.usage, pair => pair.texture), values);
    }

    private static (byte[] Expanded, List<ObjectBufferConfiguration> Buffers) PrivateObjectBuffers(
        ReadOnlySpan<byte> original, string namespaceName, IReadOnlyCollection<uint> sourceNames)
    {
        var parsed = ParseCpuLayout(original);
        var sourceNameSet = sourceNames.ToHashSet();
        var controls = ControlHashes.ToHashSet();
        uint[]? donorDescriptor = null;
        byte[]? donorFields = null;
        foreach (var block in parsed.Blocks)
        foreach (var fields in block.CbFields)
        {
            var raw = original[fields.At..(fields.At + fields.Count * 20)];
            var names = new HashSet<uint>();
            for (var at = 0; at < raw.Length; at += 20) names.Add(U32(raw, at + 8));
            if (!controls.IsSubsetOf(names)) continue;
            donorDescriptor = new uint[6];
            for (var field = 0; field < donorDescriptor.Length; field++)
                donorDescriptor[field] = U32(original, block.CbAt + fields.Index * 24 + field * 4);
            donorFields = raw.ToArray();
            break;
        }
        IasBinary.Require(donorDescriptor is not null && donorFields is not null, "Material has no reviewed c_per_object scalar definition");
        var originalSize = donorDescriptor[4];
        IasBinary.Require(originalSize > 0 && originalSize % 16 == 0, "Unaligned object constant-buffer size");
        var offsets = Enumerable.Range(0, 6).Select(index => (int)originalSize + index * 4).ToArray();
        var newSize = Align((int)originalSize + 24, 16);
        IasBinary.Require(newSize <= 65536, "Extended object constant buffer exceeds D3D11 capacity");
        var output = new List<byte>(original[..parsed.Blocks[0].Start].ToArray());
        void Append(ReadOnlySpan<byte> raw)
        {
            while (output.Count % 8 != 0) output.Add(0);
            output.AddRange(raw.ToArray());
        }
        var configurations = new List<ObjectBufferConfiguration>();
        foreach (var block in parsed.Blocks)
        {
            var entries = block.Entries.Select(entry => (Name: entry.Name, Kind: entry.Kind, Offset: entry.Offset)).ToList();
            var inputIndices = entries.Select((entry, index) => (entry, index)).Where(item => item.entry.Name == IasHash.Murmur32("c_per_object"))
                .Select(item => item.index).ToList();
            IasBinary.Require(inputIndices.Count <= 1, "Ambiguous object constant-buffer input");
            var descriptors = new List<uint[]>(block.CbCount);
            for (var index = 0; index < block.CbCount; index++)
            {
                var descriptorRow = new uint[6];
                for (var field = 0; field < descriptorRow.Length; field++)
                    descriptorRow[field] = U32(original, block.CbAt + index * 24 + field * 4);
                descriptors.Add(descriptorRow);
            }
            var fieldData = new List<byte[]>(block.CbFields.Count);
            foreach (var fields in block.CbFields) fieldData.Add(original[fields.At..(fields.At + fields.Count * 20)].ToArray());
            var objectIndices = new List<int>();
            for (var index = 0; index < fieldData.Count; index++)
            {
                var names = new HashSet<uint>();
                var raw = fieldData[index];
                for (var at = 0; at < raw.Length; at += 20) names.Add(U32(raw, at + 8));
                if (controls.IsSubsetOf(names)) objectIndices.Add(index);
            }
            IasBinary.Require(objectIndices.Count <= 1, "Ambiguous native scalar fields");
            int objectIndex, inputIndex;
            if (inputIndices.Count > 0)
            {
                IasBinary.Require(objectIndices.Count == 1, "Existing object buffer lacks the six reviewed scalar fields");
                objectIndex = objectIndices[0];
                inputIndex = inputIndices[0];
                IasBinary.Require(entries[inputIndex].Kind == 0, "Object constant-buffer input has an unsupported kind");
            }
            else
            {
                IasBinary.Require(objectIndices.Count == 0, "Unbound object scalar definition");
                objectIndex = descriptors.Count;
                inputIndex = entries.Count;
                descriptors.Add((uint[])donorDescriptor.Clone());
                fieldData.Add((byte[])donorFields!.Clone());
                entries.Add((IasHash.Murmur32("c_per_object"), 0U, block.ParameterSize));
            }
            var descriptor = descriptors[objectIndex];
            IasBinary.Require(descriptor[4] == originalSize, "Inconsistent object-buffer layout across techniques");
            var records = new List<uint[]>();
            for (var at = 0; at < fieldData[objectIndex].Length; at += 20)
                records.Add(Enumerable.Range(0, 5).Select(field => U32(fieldData[objectIndex], at + field * 4)).ToArray());
            var recordNames = records.Select(record => record[2]).ToHashSet();
            var oldFields = 0;
            foreach (var record in records)
            {
                if (!controls.Contains(record[2])) continue;
                IasBinary.Require(record[0] == 0 && record[1] == 0 && record[4] == 4, "Non-scalar native morph parameter");
                var renamed = IasHash.Murmur32(namespaceName + "/original_scalar/" + record[2].ToString(CultureInfo.InvariantCulture));
                IasBinary.Require(renamed != 0 && !recordNames.Contains(renamed) && !sourceNameSet.Contains(renamed), "Private scalar name collision");
                recordNames.Add(renamed);
                record[2] = renamed;
                oldFields++;
            }
            IasBinary.Require(oldFields == 6, "Missing or duplicated source scalar field");
            for (var index = 0; index < 6; index++) records.Add([0, 0, ControlHashes[index], (uint)offsets[index], 4]);
            fieldData[objectIndex] = records.SelectMany(record => record.SelectMany(BitConverter.GetBytes)).ToArray();
            descriptor[2] = (uint)records.Count;
            descriptor[4] = (uint)newSize;
            descriptor[5] = (uint)(block.ParameterSize + 24);
            entries[inputIndex] = (entries[inputIndex].Name, 0U, block.ParameterSize + 24);
            SetU32(output, block.Header + 16, (uint)entries.Count);
            SetU32(output, block.Header + 24, checked((uint)(block.ParameterSize + 24 + newSize)));
            SetU32(output, block.Header + 40, (uint)descriptors.Count);
            Append(descriptors.SelectMany(descriptor => descriptor.SelectMany(BitConverter.GetBytes)).ToArray());
            Append(original.Slice(block.ProgramsAt, block.ProgramCount * 1040));
            Append(original.Slice(block.TextureDefaultsAt, block.TextureDefaultCount * 24));
            Append(entries.SelectMany(entry => BitConverter.GetBytes(entry.Name).Concat(BitConverter.GetBytes(entry.Kind))
                .Concat(BitConverter.GetBytes(entry.Offset))).ToArray());
            foreach (var raw in fieldData) Append(raw);
            foreach (var vertex in block.Vertex) Append(original.Slice(vertex.At, vertex.Count * 20));
            configurations.Add(new ObjectBufferConfiguration(inputIndex, newSize, offsets));
        }
        var oldTail = checked((int)U32(original, 12));
        output.AddRange(original[parsed.End..oldTail].ToArray());
        var difference = output.Count - oldTail;
        IasBinary.Require(difference % 8 == 0, "CPU material alignment changed");
        output.AddRange(original[oldTail..].ToArray());
        foreach (var field in new[] { 12, 20, 28, 40 }) SetU32(output, field, checked(U32(original, field) + (uint)difference));
        SetU32(output, 8, (uint)(output.Count - 8));
        var expanded = output.ToArray();
        ParseCpuLayout(expanded);
        return (expanded, configurations);
    }

    private sealed record ObjectBufferConfiguration(int InputIndex, int BufferSize, int[] WeightOffsets);
    private sealed record CpuField(int Index, int At, int Count);
    private sealed record CpuVertex(int At, int Count);
    private sealed record CpuBlock(int Block, int Header, int Start, int End, int InputCount, int ParameterSize,
        int CbAt, int CbCount, int TextureDefaultsAt, int TextureDefaultCount, int ProgramsAt, int ProgramCount,
        List<(uint Name, uint Kind, int Offset)> Entries, List<CpuField> CbFields, List<CpuVertex> Vertex);
    private sealed record CpuLayout(List<CpuBlock> Blocks, int End);
    private sealed class GpuStage
    {
        public required byte[] Code { get; set; }
        public required uint Name { get; set; }
        public required List<byte[]> Metadata { get; init; }
    }
    private sealed class GpuProgram
    {
        public required byte[] Header { get; init; }
        public required byte[] States { get; init; }
        public required List<byte[]> Attrs { get; init; }
        public required List<byte[]> Samplers { get; init; }
        public required List<GpuStage> Stages { get; init; }
        public required byte[] Vertex { get; init; }
    }
    private sealed record GpuRow(int Block, int Program, int Header, int StatesAt, int StatesEnd, int End);
    private sealed record GpuShaderRow(int Block, int Program, int Header, int Offset, int Size, int MetadataStart,
        int BufferCount, int ObjectCount, int SamplerCount);
    private sealed record TechniqueLayout(int Base, int End, int Count, int Routes, int RoutePosition, int Cursor,
        List<int> Headers, List<int> References);

    private static CpuLayout ParseCpuLayout(ReadOnlySpan<byte> data)
    {
        var limit = checked((int)U32(data, 28));
        var count = U32(data, 44);
        IasBinary.Require(data.Length >= 48 && U32(data, 0) == 179 && count is > 0 and <= 256, "未核对的 CPU 技术格式。");
        var cursor = Align(48 + (int)count * 88, 8);
        var blocks = new List<CpuBlock>();
        for (var blockIndex = 0; blockIndex < count; blockIndex++)
        {
            var header = 48 + blockIndex * 88;
            var inputCount = (int)U32(data, header + 16);
            var parameterSize = (int)U32(data, header + 24);
            var cbCount = (int)U32(data, header + 40);
            var textureCount = (int)U32(data, header + 56);
            var programCount = (int)U32(data, header + 80);
            IasBinary.Require(new[] { inputCount, cbCount, textureCount, programCount }.All(value => value is >= 0 and <= 1024), "CPU 技术计数越界");
            cursor = Align(cursor, 8);
            var start = cursor;
            var cbAt = cursor;
            Bounded(data, cursor, cbCount * 24, limit);
            cursor = Align(cursor + cbCount * 24, 8);
            var programsAt = cursor;
            Bounded(data, cursor, programCount * 1040, limit);
            cursor = Align(cursor + programCount * 1040, 8);
            var textureAt = cursor;
            Bounded(data, cursor, textureCount * 24, limit);
            cursor = Align(cursor + textureCount * 24, 8);
            var inputsAt = cursor;
            Bounded(data, cursor, inputCount * 12, limit);
            var entries = new List<(uint Name, uint Kind, int Offset)>();
            for (var index = 0; index < inputCount; index++)
            {
                var at = inputsAt + index * 12;
                var entry = (U32(data, at), U32(data, at + 4), checked((int)U32(data, at + 8)));
                IasBinary.Require(entry.Item2 is 0 or 1 or 2 or 4 && entry.Item3 < parameterSize, "CPU 输入定义无效");
                entries.Add(entry);
            }
            cursor += inputCount * 12;
            var fields = new List<CpuField>();
            for (var index = 0; index < cbCount; index++)
            {
                cursor = Align(cursor, 8);
                var fieldCount = (int)U32(data, cbAt + index * 24 + 8);
                Bounded(data, cursor, fieldCount * 20, limit);
                var bufferSize = U32(data, cbAt + index * 24 + 16);
                for (var member = 0; member < fieldCount; member++)
                {
                    var at = cursor + member * 20;
                    var kind = U32(data, at);
                    var offset = U32(data, at + 12);
                    var size = U32(data, at + 16);
                    IasBinary.Require(kind <= 15 && offset <= bufferSize && size <= bufferSize - offset, "CPU 常量字段越界");
                }
                fields.Add(new CpuField(index, cursor, fieldCount));
                cursor += fieldCount * 20;
            }
            var vertex = new List<CpuVertex>();
            for (var index = 0; index < programCount; index++)
            {
                cursor = Align(cursor, 8);
                var vertexCount = (int)U32(data, programsAt + index * 1040 + 32);
                Bounded(data, cursor, vertexCount * 20, limit);
                vertex.Add(new CpuVertex(cursor, vertexCount));
                cursor += vertexCount * 20;
            }
            blocks.Add(new CpuBlock(blockIndex, header, start, cursor, inputCount, parameterSize, cbAt, cbCount,
                textureAt, textureCount, programsAt, programCount, entries, fields, vertex));
        }
        var sectionTail = checked((int)U32(data, 12));
        IasBinary.Require(sectionTail == cursor || sectionTail == Align(cursor, 8), "CPU 技术尾部位置不一致");
        IasBinary.Require(sectionTail + 8 == limit && !data[cursor..sectionTail].ToArray().Any(value => value != 0), "CPU 技术尾部存在意外数据");
        return new CpuLayout(blocks, cursor);
    }

    private static void Bounded(ReadOnlySpan<byte> data, int at, int size, int limit)
        => IasBinary.Require(size >= 0 && at >= 0 && at <= data.Length && size <= data.Length - at && at + size <= limit, "CPU 技术区越界");

    private static List<List<GpuProgram>> Models(ReadOnlySpan<byte> gpu)
    {
        var (_, programs) = ReadGpuLayout(gpu);
        var start = checked((int)U32(gpu, 20));
        var blockCount = (int)U32(gpu, start + 8);
        var grouped = Enumerable.Range(0, blockCount).Select(_ => new List<GpuProgram>()).ToList();
        foreach (var program in programs)
        {
            var header = gpu.Slice(program.Header, 544).ToArray();
            var at = Align(program.StatesEnd, 8);
            var attrCount = (int)U32(header, 24);
            var attrs = new List<byte[]>();
            for (var index = 0; index < attrCount; index++) attrs.Add(gpu.Slice(at + index * 24, 24).ToArray());
            at += attrCount * 24;
            var samplers = new List<byte[]>();
            foreach (var attr in attrs)
            {
                at = Align(at, 8);
                var count = (int)U32(attr, 8);
                samplers.Add(gpu.Slice(at, count * 16).ToArray());
                at += count * 16;
            }
            at = Align(at, 8);
            var stages = new List<GpuStage>();
            for (var stageIndex = 0; stageIndex < 6; stageIndex++)
            {
                var descriptor = 32 + stageIndex * 80;
                var size = (int)U32(header, descriptor + 8);
                var name = U32(header, descriptor + 12);
                var code = gpu.Slice(at, size).ToArray();
                at = Align(at + size, 4);
                IasBinary.Require(U32(gpu, at) == name, "Unsupported native material or shader layout");
                at = Align(at + 4, 8);
                var metadata = new List<byte[]>();
                foreach (var (field, stride) in new[] { (24, 16), (40, 16), (56, 16), (72, 12) })
                {
                    var count = (int)U32(header, descriptor + field);
                    metadata.Add(gpu.Slice(at, count * stride).ToArray());
                    at = Align(at + count * stride, 8);
                }
                stages.Add(new GpuStage { Code = code, Name = name, Metadata = metadata });
            }
            var vertexCount = (int)U32(header, 536);
            var vertex = gpu.Slice(at, vertexCount * 20).ToArray();
            IasBinary.Require(at + vertex.Length == program.End, "Unsupported native material or shader layout");
            grouped[program.Block].Add(new GpuProgram { Header = header, States = gpu.Slice(program.StatesAt, program.StatesEnd - program.StatesAt).ToArray(),
                Attrs = attrs, Samplers = samplers, Stages = stages, Vertex = vertex });
        }
        return grouped;
    }

    private static (List<GpuShaderRow> Rows, List<GpuRow> Programs) ReadGpuLayout(ReadOnlySpan<byte> data)
    {
        IasBinary.Require(data.Length >= 48 && U32(data, 0) == 179, "未适配的 GPU 布局版本");
        var start = checked((int)U32(data, 20));
        var count = checked((int)U32(data, start + 8));
        IasBinary.Require(count is > 0 and <= 256, "GPU 程序组数量异常");
        var table = Align(start + 104, 8);
        var counts = new int[count];
        for (var index = 0; index < count; index++) counts[index] = checked((int)U32(data, table + index * 16));
        IasBinary.Require(counts.All(value => value <= 256), "GPU 程序数量异常");
        var position = table + (int)count * 16 - 8;
        var programs = new List<GpuRow>();
        var rows = new List<GpuShaderRow>();
        for (var block = 0; block < counts.Length; block++)
        {
            position = Align(position, 8);
            var firstHeader = position;
            position = BoundedGpu(data, position + (int)counts[block] * 544);
            for (var programIndex = 0; programIndex < counts[block]; programIndex++)
            {
                var header = firstHeader + programIndex * 544;
                position = Align(position, 8);
                var statesAt = position;
                var stateCount = (int)U32(data, header + 8);
                IasBinary.Require(stateCount <= 4096, "GPU 状态表数量异常");
                position = BoundedGpu(data, position + stateCount * 16);
                position = Align(position, 8);
                var texturesAt = position;
                var textureCount = (int)U32(data, header + 24);
                IasBinary.Require(textureCount <= 1024, "GPU 纹理状态数量异常");
                position = BoundedGpu(data, position + textureCount * 24);
                for (var index = 0; index < textureCount; index++)
                {
                    position = Align(position, 8);
                    var samplerCount = (int)U32(data, texturesAt + index * 24 + 8);
                    IasBinary.Require(samplerCount <= 1024, "GPU 采样器状态数量异常");
                    position = BoundedGpu(data, position + samplerCount * 16);
                }
                position = Align(position, 8);
                var metadataStart = position;
                for (var stage = 0; stage < 6; stage++)
                {
                    var descriptor = header + 32 + stage * 80;
                    var size = (int)U32(data, descriptor + 8);
                    var name = U32(data, descriptor + 12);
                    var offset = position;
                    position = BoundedGpu(data, Align(position + size, 4));
                    IasBinary.Require(U32(data, position) == name, "GPU 阶段尾部标识与头部不同");
                    position = Align(position + 4, 8);
                    var metadataCounts = new int[4];
                    var metadataFields = new[] { 24, 40, 56, 72 };
                    for (var field = 0; field < metadataFields.Length; field++)
                        metadataCounts[field] = checked((int)U32(data, descriptor + metadataFields[field]));
                    IasBinary.Require(metadataCounts.All(value => value <= 1024), "GPU 绑定表数量异常");
                    foreach (var (value, stride) in metadataCounts.Zip(new[] { 16, 16, 16, 12 }))
                        position = BoundedGpu(data, Align(position + value * stride, 8));
                    if (size > 0)
                    {
                        IasBinary.Require(data.Slice(offset, 4).SequenceEqual("DXBC"u8) && U32(data, offset + 24) == size, "GPU 程序未指向 DXBC");
                        IasBinary.Require(metadataCounts[2] == 0, "当前后端尚未支持 UAV 元数据");
                        rows.Add(new GpuShaderRow(block, programIndex, descriptor + 8, offset, size, metadataStart,
                            metadataCounts[0], metadataCounts[1], metadataCounts[3]));
                    }
                    else IasBinary.Require(metadataCounts.All(value => value == 0), "空阶段带有非空绑定表");
                    metadataStart = position;
                }
                var vertexCount = (int)U32(data, header + 536);
                IasBinary.Require(vertexCount <= 1024, "GPU 顶点布局数量异常");
                position = BoundedGpu(data, position + vertexCount * 20);
                programs.Add(new GpuRow(block, programIndex, header, statesAt, position, position));
            }
        }
        IasBinary.Require(data.Length - position <= 16 && !data[position..].ToArray().Any(value => value != 0), "GPU 末尾未被完整解析");
        return (rows, programs);
    }

    private static int BoundedGpu(ReadOnlySpan<byte> data, int at)
    {
        IasBinary.Require(at >= 0 && at <= data.Length, "GPU 数据游标越界");
        return at;
    }

    private static byte[] Serialize(ReadOnlySpan<byte> gpu, List<List<GpuProgram>> grouped)
    {
        var start = checked((int)U32(gpu, 20));
        var output = new List<byte>(gpu[..(start + 96)].ToArray());
        Pad(output, 8);
        var originalTable = Align(start + 96, 8);
        for (var index = 0; index < grouped.Count; index++)
        {
            var record = gpu.Slice(originalTable + index * 16, 16).ToArray();
            WriteU32(record, 8, (uint)grouped[index].Count);
            output.AddRange(record);
        }
        for (var block = 0; block < grouped.Count; block++)
        {
            Pad(output, 8);
            var headersAt = output.Count;
            foreach (var program in grouped[block]) output.AddRange(program.Header);
            for (var index = 0; index < grouped[block].Count; index++)
            {
                var program = grouped[block][index];
                var headerAt = headersAt + index * 544;
                Pad(output, 8);
                output.AddRange(program.States);
                Pad(output, 8);
                SetU32(output, headerAt + 24, (uint)program.Attrs.Count);
                foreach (var attr in program.Attrs) output.AddRange(attr);
                foreach (var sampler in program.Samplers) { Pad(output, 8); output.AddRange(sampler); }
                Pad(output, 8);
                for (var stage = 0; stage < 6; stage++)
                {
                    var descriptor = headerAt + 32 + stage * 80;
                    var value = program.Stages[stage];
                    SetU32(output, descriptor + 8, (uint)value.Code.Length);
                    SetU32(output, descriptor + 12, value.Name);
                    output.AddRange(value.Code);
                    Pad(output, 4);
                    output.AddRange(BitConverter.GetBytes(value.Name));
                    Pad(output, 8);
                    foreach (var (field, stride, payload) in new[] { (24, 16, value.Metadata[0]), (40, 16, value.Metadata[1]),
                                 (56, 16, value.Metadata[2]), (72, 12, value.Metadata[3]) })
                    {
                        IasBinary.Require(payload.Length % stride == 0, "Unsupported native material or shader layout");
                        SetU32(output, descriptor + field, (uint)(payload.Length / stride));
                        output.AddRange(payload);
                        Pad(output, 8);
                    }
                }
                output.AddRange(program.Vertex);
            }
        }
        Pad(output, 8);
        SetU32(output, 8, (uint)(output.Count - 8));
        SetU32(output, 24, checked((uint)(output.Count - start - 8)));
        var result = output.ToArray();
        ParseGpu(result);
        return result;
    }

    private static byte[] IsolateTechniques(byte[] gpu, string namespaceName)
    {
        var layout = TechniqueLayoutOf(gpu);
        var identities = layout.Headers.Select(header => U32(gpu, header)).Distinct().ToHashSet();
        var mapping = identities.ToDictionary(identity => identity,
            identity => IasHash.Murmur32(namespaceName + "/technique/" + identity.ToString(CultureInfo.InvariantCulture)));
        IasBinary.Require(mapping.Values.All(value => value != 0) && mapping.Values.Distinct().Count() == mapping.Count, "Private technique identity collision");
        var result = (byte[])gpu.Clone();
        foreach (var field in layout.Headers.Concat(layout.References)) WriteU32(result, field, mapping[U32(gpu, field)]);
        IasBinary.Require(result.AsSpan(layout.End).SequenceEqual(gpu.AsSpan(layout.End)), "Technique isolation changed shader bytes");
        return result;
    }

    private static TechniqueLayout TechniqueLayoutOf(byte[] gpu)
    {
        var baseOffset = (int)U32(gpu, 28);
        var end = (int)U32(gpu, 20);
        var count = (int)U32(gpu, 44);
        var routes = (int)U32(gpu, baseOffset + 20);
        var position = Align(baseOffset + 35, 8);
        IasBinary.Require(routes is > 0 and <= 256 && position + 24 * routes <= end, "Unsupported technique route table");
        var headers = Enumerable.Range(0, count).Select(index => 48 + index * 88).ToList();
        var cursor = position + 24 * routes;
        var references = new List<int>();
        for (var index = 0; index < routes; index++)
        {
            var number = (int)U32(gpu, position + index * 24 + 16);
            IasBinary.Require(number <= count && cursor + 24 * number <= end, "Technique route range");
            var fields = Enumerable.Range(0, number).Select(offset => cursor + offset * 24).ToList();
            references.AddRange(fields);
            cursor += 24 * number + fields.Sum(field => (int)U32(gpu, field + 16));
            IasBinary.Require(cursor <= end, "Technique condition range");
        }
        IasBinary.Require(headers.Select(header => U32(gpu, header)).OrderBy(value => value)
            .SequenceEqual(references.Select(field => U32(gpu, field)).OrderBy(value => value)), "Technique references do not cover native definitions");
        IasBinary.Require(end - cursor is >= 0 and < 8 && !gpu[cursor..end].Any(value => value != 0), "Unexpected technique padding");
        return new TechniqueLayout(baseOffset, end, count, routes, position, cursor, headers, references);
    }

    private static uint ContentId(byte[] data)
    {
        ulong value = (ulong)data.Length * 14313749767032793493UL;
        var end = data.Length / 8 * 8;
        for (var offset = 0; offset < end; offset += 8)
        {
            var word = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
            word = unchecked(word * 14313749767032793493UL);
            word ^= word >> 47;
            word = unchecked(word * 14313749767032793493UL);
            value = unchecked((value ^ word) * 14313749767032793493UL);
        }
        if (end < data.Length)
        {
            ulong tail = 0;
            for (var index = 0; index < data.Length - end; index++) tail |= (ulong)data[end + index] << (8 * index);
            value = unchecked((value ^ tail) * 14313749767032793493UL);
        }
        value ^= value >> 47;
        value = unchecked(value * 14313749767032793493UL);
        value ^= value >> 47;
        return (uint)(value >> 32) is var result and > 0 ? result : 1;
    }

    private static byte[] BindMaterialId(byte[] gpu, ulong fileId)
    {
        ParseGpu(gpu);
        var name = (uint)(fileId >> 32);
        WriteU32(gpu, 4, name);
        WriteU32(gpu, checked((int)U32(gpu, 28)) + 4, name);
        ParseGpu(gpu);
        return gpu;
    }

    public static void ValidateMaterials(IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> materials)
    {
        var identities = new Dictionary<uint, string>();
        foreach (var (key, resource) in materials)
        {
            if (key.Type != IasResourceTypes.Material) continue;
            ValidateEntry(key.Id, resource);
            var (rows, _) = ReadGpuLayout(resource.Gpu.Span);
            foreach (var row in rows)
            {
                var code = resource.Gpu.Span.Slice(row.Offset, row.Size).ToArray();
                var digest = IasHash.Sha256Hex(code);
                var name = U32(resource.Gpu.Span, row.Header + 4);
                IasBinary.Require(name == ContentId(code), "私有材质的着色程序内容标识不一致。");
                if (identities.TryGetValue(name, out var previous))
                    IasBinary.Require(previous == digest, "不同着色程序使用了相同标识，不能加入 Patch。");
                identities[name] = digest;
            }
        }
    }

    private static void ValidateEntry(ulong id, IasResource resource)
    {
        IasBinary.Require(resource.Toc.Length >= 24 && BinaryPrimitives.ReadUInt64LittleEndian(resource.Toc.AsSpan(16)) == (ulong)resource.Gpu.Length,
            "材质头记录的 GPU 长度与实际数据不一致。");
        ParseCpuLayout(resource.Gpu.Span);
        ParseGpu(resource.Gpu.Span);
        IasBinary.Require(U32(resource.Gpu.Span, 4) == (uint)(id >> 32), "材质 FileID 与 GPU 内部标识不匹配。");
    }

    private static void ParseGpu(ReadOnlySpan<byte> data)
    {
        IasBinary.Require(data.Length >= 48 && U32(data, 0) == 179, "尚未适配此原生材质 GPU 格式。");
        var programOffset = checked((int)U32(data, 20));
        IasBinary.Require((long)U32(data, 8) == data.Length - 8 && (long)U32(data, 24) == data.Length - programOffset - 8, "原生材质 GPU 总长度不一致。");
        IasBinary.Require(U32(data, 4) == U32(data, checked((int)U32(data, 28)) + 4), "原生材质头与名称表标识不一致。");
        var (rows, _) = ReadGpuLayout(data);
        foreach (var row in rows)
        {
            var code = data.Slice(row.Offset, row.Size).ToArray();
            var rdef = ShaderChunk(code, "RDEF");
            var buffers = U32(rdef, 0);
            var bufferOffset = U32(rdef, 4);
            var count = U32(rdef, 8);
            var offset = U32(rdef, 12);
            IasBinary.Require(buffers <= 128 && count <= 1024 && offset + count * 32 <= rdef.Length && bufferOffset + buffers * 24 <= rdef.Length,
                "原生着色器反射表越界。");
            var kinds = Enumerable.Range(0, (int)count).Select(index => U32(rdef, offset + index * 32 + 4)).ToArray();
            IasBinary.Require(buffers == row.BufferCount && kinds.LongCount(kind => kind == 2) == row.ObjectCount && kinds.LongCount(kind => kind == 3) == row.SamplerCount,
                "程序描述符与 RDEF 资源数量不同。");
        }
    }

    private static byte[] ShaderChunk(byte[] data, string tag)
    {
        IasBinary.Require(data.Length >= 32 && data.AsSpan(0, 4).SequenceEqual("DXBC"u8), "不是 DXBC 容器。");
        var size = U32(data, 24);
        var count = checked((int)U32(data, 28));
        for (var index = 0; index < count; index++)
        {
            var offset = checked((int)U32(data, 32 + index * 4));
            if (Encoding.ASCII.GetString(data, offset, 4) != tag) continue;
            var length = U32(data, offset + 4);
            IasBinary.Require(offset + 8L + length <= size, "DXBC Chunk 越界。");
            return data.AsSpan(offset + 8, (int)length).ToArray();
        }
        throw new InvalidDataException("原生着色器缺少 RDEF 反射信息。");
    }

    private static bool ByteListsEqual(IReadOnlyList<byte[]> left, IReadOnlyList<byte[]> right)
        => left.Count == right.Count && left.Zip(right).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second));

    private static void Pad(List<byte> data, int alignment)
    {
        while (data.Count % alignment != 0) data.Add(0);
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static uint U32(ReadOnlySpan<byte> data, uint offset) => U32(data, checked((int)offset));
    private static uint U32(ReadOnlySpan<byte> data, long offset) => U32(data, checked((int)offset));
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    private static void WriteU32(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, 4), value);
    private static void WriteU32(Span<byte> data, uint offset, uint value) => WriteU32(data, checked((int)offset), value);
    private static void WriteU32(Span<byte> data, long offset, uint value) => WriteU32(data, checked((int)offset), value);
    private static void WriteU32(byte[] data, int offset, uint value) => WriteU32(data.AsSpan(), offset, value);
    private static void SetU32(List<byte> data, int offset, uint value)
    {
        while (data.Count < offset + 4) data.Add(0);
        WriteU32(CollectionsMarshal.AsSpan(data), offset, value);
    }
}
