using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record ShaderInjection(byte[] Code, bool AddedObjectBufferBinding, int Slot, int ObjectBufferSize, int[] WeightOffsets);

/// <summary>原生形态材质所需的 DXBC 注入与 D3D11 WARP 校验。</summary>
public static unsafe partial class IasNativeMorphShader
{
    private static readonly Regex Stage = new(@"^\s*[pvghdc]s_\d_\d\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex InputRegister = new(@"\bv(\d+)\.", RegexOptions.Compiled);
    private static readonly Regex ConstantBuffer = new(@"dcl_constantbuffer CB(\d+)\[", RegexOptions.Compiled);

    public sealed class DeviceValidator : IDisposable
    {
        private IntPtr _device;
        private IntPtr _context;
        private bool _disposed;

        public DeviceValidator()
        {
            var levels = stackalloc uint[] { 45056 };
            var hr = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, levels, 1, 7,
                out _device, out _, out _context);
            if (hr < 0) throw new InvalidDataException($"D3D11 WARP 设备创建失败：0x{hr & 0xFFFFFFFFU:x8}");
        }

        public void Validate(byte[] data, string stage)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fixed (byte* pointer = data)
            {
                var device = (ID3D11Device)Marshal.GetObjectForIUnknown(_device);
                IntPtr shader;
                int hr;
                try
                {
                    hr = stage == "vs"
                        ? device.CreateVertexShader((IntPtr)pointer, (UIntPtr)data.Length, IntPtr.Zero, out shader)
                        : device.CreatePixelShader((IntPtr)pointer, (UIntPtr)data.Length, IntPtr.Zero, out shader);
                }
                finally { Marshal.ReleaseComObject(device); }
                if (hr < 0) throw new InvalidDataException($"Direct3D 拒绝 {stage} 字节码：0x{hr & 0xFFFFFFFFU:x8}");
                Release(shader);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(_context);
            Release(_device);
            _context = _device = IntPtr.Zero;
        }

        private static void Release(IntPtr pointer) => IasNativeMorphShader.ReleaseCom(pointer);

    }

    [LibraryImport("d3dcompiler_47.dll")]
    private static partial int D3DCompile(byte* source, nuint size, byte* fileName, IntPtr defines,
        IntPtr include, byte* entryPoint, byte* target, uint flags1, uint flags2,
        out IntPtr code, out IntPtr errors);

    [LibraryImport("d3dcompiler_47.dll")]
    private static partial int D3DDisassemble(byte* data, nuint size, uint flags, byte* comments, out IntPtr disassembly);

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags,
        uint* featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device,
        out uint featureLevel, out IntPtr context);

    public static byte[] CompileHlsl(string source, string target = "ps_5_0")
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* sourcePointer = bytes)
        {
            var fileName = "liltoon_hd2_native.hlsl\0"u8;
            var entry = "main\0"u8;
            var targetBytes = Encoding.ASCII.GetBytes(target + "\0");
            fixed (byte* fileNamePointer = fileName)
            fixed (byte* entryPointer = entry)
            fixed (byte* targetPointer = targetBytes)
            {
                var hr = D3DCompile(sourcePointer, (nuint)bytes.Length, fileNamePointer, IntPtr.Zero, IntPtr.Zero,
                    entryPointer, targetPointer, 34816, 0, out var code, out var errors);
                var message = errors == IntPtr.Zero ? string.Empty : Encoding.UTF8.GetString(BlobBytes(errors)).TrimEnd('\0');
                if (errors != IntPtr.Zero) ReleaseBlob(errors);
                if (hr < 0) throw new InvalidDataException(message.Length > 0 ? message : $"D3DCompile HRESULT 0x{hr & 0xFFFFFFFFU:x8}");
                try { return BlobBytes(code); }
                finally { ReleaseBlob(code); }
            }
        }
    }

    public static string Disassemble(byte[] data)
    {
        fixed (byte* pointer = data)
        {
            var hr = D3DDisassemble(pointer, (nuint)data.Length, 0, null, out var result);
            if (hr < 0)
            {
                if (result != IntPtr.Zero) ReleaseBlob(result);
                throw new InvalidDataException($"无法反汇编 DXBC：0x{hr & 0xFFFFFFFFU:x8}");
            }
            try { return Encoding.UTF8.GetString(BlobBytes(result)).TrimEnd('\0'); }
            finally { ReleaseBlob(result); }
        }
    }

    public static ShaderInjection Inject(byte[] code, int[] offsets, DeviceValidator validator)
    {
        var originalInputs = Signature(code, "ISGN");
        var outputs = Signature(code, "OSGN");
        var native = Bindings(code).FirstOrDefault(binding => binding.Kind == 0 && binding.Name == "c_per_object");
        var originalBuffer = ObjectReflection(code);
        IasBinary.Require((originalBuffer is not null) == (native is not null), "Object buffer reflection/binding mismatch");
        if (originalBuffer is not null)
        {
            IasBinary.Require(originalBuffer.Size <= offsets.Min(), "Morph tail overlaps original reflected fields");
            IasBinary.Require(!originalBuffer.Variables.Any(variable => variable.Name is "w0" or "w1" or "w2" or "w3" or "w4" or "w5"),
                "Original object buffer occupies helper variable names");
        }
        int slot;
        if (native is not null) slot = native.Slot;
        else
        {
            var used = Bindings(code).Where(binding => binding.Kind == 0).Select(binding => binding.Slot).ToHashSet();
            var available = Enumerable.Range(0, 14).Where(index => !used.Contains(index)).Order().ToList();
            IasBinary.Require(available.Count > 0, "No native constant-buffer slot available");
            slot = available[0];
        }
        IasBinary.Require(!originalInputs.Any(input => input.Semantic == "TEXCOORD" && input.Index is >= 3 and <= 7),
            "Original VS occupies reserved morph TEXCOORD3..7");
        var helper = CompileHlsl(HelperSource(slot, offsets), "vs_5_0");
        var (helperHeader, helperRows) = InstructionRows(helper);
        var (header, rows) = InstructionRows(code);
        IasBinary.Require(header.Version == helperHeader.Version, "Unsupported native material or shader layout");
        var temps = rows.Where(row => row.Line.StartsWith("dcl_temps ", StringComparison.Ordinal))
            .Select(row => int.Parse(row.Line["dcl_temps ".Length..], CultureInfo.InvariantCulture)).ToList();
        IasBinary.Require(temps.Count <= 1, "Unsupported native material or shader layout");
        var tempBase = temps.Count == 1 ? temps[0] : 0;
        var helperTemps = helperRows.FirstOrDefault(row => row.Line.StartsWith("dcl_temps ", StringComparison.Ordinal));
        var helperTempCount = helperTemps.Line is null ? 0 : int.Parse(helperTemps.Line["dcl_temps ".Length..], CultureInfo.InvariantCulture);
        var inputRows = originalInputs.Select(row => new SignatureRow
        {
            Semantic = row.Semantic, Index = row.Index, System = row.System, ComponentType = row.ComponentType,
            Register = row.Register, Mask = row.Mask, Used = row.Used
        }).ToList();
        var bySemantic = inputRows.ToDictionary(row => (row.Semantic, row.Index));
        var mapping = new Dictionary<int, (int Kind, int Number)>();
        var nextRegister = inputRows.Count == 0 ? 0 : inputRows.Max(row => row.Register) + 1;
        foreach (var input in Signature(helper, "ISGN"))
        {
            var key = (input.Semantic, input.Index);
            if (!bySemantic.TryGetValue(key, out var target))
            {
                IasBinary.Require(input.Semantic == "TEXCOORD" && input.Index is >= 0 and <= 7, "Unsupported native material or shader layout");
                target = new SignatureRow
                {
                    Semantic = input.Semantic, Index = input.Index, System = input.System,
                    ComponentType = input.ComponentType, Register = nextRegister++, Mask = input.Mask, Used = input.Used
                };
                inputRows.Add(target);
                bySemantic[key] = target;
            }
            target.Mask |= input.Mask;
            target.Used |= input.Used;
            mapping[input.Register] = (1, target.Register);
        }
        IasBinary.Require(nextRegister <= 32, "Unsupported native material or shader layout");
        var outputMap = Signature(helper, "OSGN").ToDictionary(output => output.Register,
            output => (Kind: 0, Number: output.Semantic == "SV_POSITION" ? tempBase : tempBase + 1));
        var prep = helperRows.Where(row => !row.Line.StartsWith("dcl_", StringComparison.Ordinal) && row.Line != "ret")
            .Select(row => Remap(row.Words, tempBase + 2, mapping, outputMap, null)).ToList();
        var originalMap = originalInputs.ToDictionary(row => row.Register, row => (Kind: 1, Number: row.Register));
        originalMap[bySemantic[("POSITION", 0)].Register] = (0, tempBase);
        originalMap[bySemantic[("NORMAL", 0)].Register] = (0, tempBase + 1);
        var originalOutputs = outputs.ToDictionary(row => row.Register, row => (Kind: 2, Number: row.Register));
        var declared = new HashSet<int>();
        var declarations = new List<uint[]>();
        foreach (var row in rows)
        {
            if (row.Line.StartsWith("dcl_", StringComparison.Ordinal) && !row.Line.StartsWith("dcl_temps ", StringComparison.Ordinal)) continue;
            var words = row.Words.ToArray();
            if (row.Line.StartsWith("dcl_input", StringComparison.Ordinal))
            {
                var match = InputRegister.Match(row.Line);
                IasBinary.Require(match.Success, "Unsupported native material or shader layout");
                var register = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                declared.Add(register);
                var target = inputRows.Single(input => input.Register == register);
                if (row.Line.StartsWith("dcl_input v", StringComparison.Ordinal))
                {
                    IasBinary.Require(words.Length == 3, "Unsupported native material or shader layout");
                    words[1] = unchecked((uint)words[1] & 0xFFFFFF0FU) | (uint)(target.Mask << 4);
                }
            }
            if (row.Line.StartsWith("dcl_constantbuffer ", StringComparison.Ordinal))
            {
                var match = ConstantBuffer.Match(row.Line);
                if (match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == slot)
                    words[^1] = Math.Max(words[^1], (uint)((offsets.Max() + 4 + 15) / 16));
            }
            declarations.Add(words);
        }
        foreach (var row in helperRows)
        {
            if (row.Line.StartsWith("dcl_input ", StringComparison.Ordinal))
            {
                var match = InputRegister.Match(row.Line);
                IasBinary.Require(match.Success, "Unsupported native material or shader layout");
                var register = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var destination = mapping[register].Number;
                if (declared.Add(destination)) declarations.Add(Remap(row.Words, inputMap: mapping));
            }
            else if (native is null && row.Line.StartsWith("dcl_constantbuffer ", StringComparison.Ordinal))
                declarations.Add(row.Words);
        }
        var template = helperRows.Single(row => row.Line.StartsWith("dcl_temps ", StringComparison.Ordinal)).Words;
        declarations.Add([template[0], (uint)(tempBase + 2 + helperTempCount)]);
        var body = rows.Where(row => !row.Line.StartsWith("dcl_", StringComparison.Ordinal))
            .Select(row => Remap(row.Words, inputMap: originalMap, outputMap: originalOutputs)).ToList();
        var combined = declarations.Concat(prep).Concat(body).ToList();
        var tokens = new List<uint> { header.Version, (uint)(2 + combined.Sum(words => words.Length)) };
        foreach (var words in combined) tokens.AddRange(words);
        var encoded = new byte[tokens.Count * 4];
        for (var index = 0; index < tokens.Count; index++) BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(encoded, index * 4, 4), tokens[index]);
        var parts = new List<(string Tag, byte[] Payload)>();
        foreach (var chunk in Chunks(code))
        {
            var (tag, sourcePayload) = chunk;
            var payload = sourcePayload;
            if (tag is "SHDR" or "SHEX") payload = encoded;
            else if (tag == "ISGN") payload = SignatureData(inputRows.OrderBy(row => row.Register).ToList());
            else if (tag == "RDEF") payload = AppendHelperCbuffer(payload, Chunk(helper, "RDEF"), native is not null);
            else if (tag == "STAT") continue;
            parts.Add((tag, payload));
        }
        var result = BuildContainer(parts);
        validator.Validate(result, "vs");
        var reflected = ObjectReflection(result) ?? throw new InvalidDataException("Private object reflection missing");
        IasBinary.Require(reflected.Size == Align(offsets.Max() + 4, 16), "Private CPU/VS object-buffer size mismatch");
        var weights = reflected.Variables.Where(variable => variable.Name is "w0" or "w1" or "w2" or "w3" or "w4" or "w5")
            .ToDictionary(variable => variable.Name, variable => variable.Offset);
        IasBinary.Require(weights.Count == 6 && offsets.Select((offset, index) => weights["w" + index] == offset).All(value => value),
            "Private weight reflection offset mismatch");
        var resultOutputs = Signature(result, "OSGN");
        IasBinary.Require(resultOutputs.Count == outputs.Count && resultOutputs.Zip(outputs).All(pair =>
            pair.First.Semantic == pair.Second.Semantic && pair.First.Index == pair.Second.Index
            && pair.First.System == pair.Second.System && pair.First.ComponentType == pair.Second.ComponentType
            && pair.First.Register == pair.Second.Register && pair.First.Mask == pair.Second.Mask && pair.First.Used == pair.Second.Used),
            "Unsupported native material or shader layout");
        IasBinary.Require(Bindings(result).Count == Bindings(code).Count + (native is null ? 1 : 0), "Unsupported native material or shader layout");
        return new ShaderInjection(result, native is null, slot, reflected.Size, offsets);
    }

    private static string HelperSource(int slot, int[] offsets)
    {
        IasBinary.Require(offsets.Length == 6 && offsets.Distinct().Count() == 6 && offsets.Min() >= 0, "Expected six distinct scalar offsets");
        string Location(int offset)
        {
            IasBinary.Require(offset % 4 == 0, "Unsupported native material or shader layout");
            return $"c{offset / 16}.{"xyzw"[offset % 16 / 4]}";
        }
        var fields = Enumerable.Range(0, 3).SelectMany(index => "zw".Select(component => $"i.uv{index}.{component}")).ToList();
        fields.AddRange("abcde".SelectMany(variable => "xyzw".Select(component => $"i.{variable}.{component}")));
        var declarations = string.Join('\n', offsets.Select((offset, index) => $"float w{index}:packoffset({Location(offset)});"));
        var positions = string.Join('\n', Enumerable.Range(0, 6).Select(index =>
            $"o.p.xyz+=w{index}*float3({string.Join(',', fields.Skip(index * 3).Take(3))});"));
        var normals = string.Join('+', Enumerable.Range(0, 6).Select(index => $"w{index}*delta({fields[18 + index]},base)"));
        return $"cbuffer c_per_object:register(b{slot}){{{declarations}\n}};\n" +
               "struct Input{float4 p:POSITION;float4 n:NORMAL;\n float4 uv0:TEXCOORD0;float4 uv1:TEXCOORD1;float4 uv2:TEXCOORD2;\n" +
               " float4 a:TEXCOORD3;float4 b:TEXCOORD4;float4 c:TEXCOORD5;float4 d:TEXCOORD6;float4 e:TEXCOORD7;};\n" +
               "struct Output{float4 p:SV_POSITION;float4 n:TEXCOORD0;};\n" +
               "float3 decode(float2 xy){\n float3 n=float3(xy,1-abs(xy.x)-abs(xy.y));float t=saturate(-n.z);\n" +
               " n.xy+=float2(n.x>=0?-t:t,n.y>=0?-t:t);return normalize(n);\n}\n" +
               "float3 delta(float packed,float3 base){\n uint p=asuint(packed);if(p==0)return float3(0,0,0);p-=1;\n" +
               " return decode(float2(p&32767,(p>>15)&32767)*(2.0/32767)-1)-base;\n}\n" +
               "Output main(Input i){\n Output o;o.p=i.p;o.n=i.n;\n" +
               " if(w0!=0||w1!=0||w2!=0||w3!=0||w4!=0||w5!=0){\n" + positions + "\n" +
               "  float3 base=decode(i.n.xy*2-1);float3 n=normalize(base+" + normals + ");\n" +
               "  n/=abs(n.x)+abs(n.y)+abs(n.z);float2 oct=n.xy;\n" +
               "  if(n.z<0)oct=(1-abs(oct.yx))*float2(oct.x>=0?1:-1,oct.y>=0?1:-1);\n" +
               "  o.n.xy=oct*.5+.5;\n }\n return o;\n}\n";
    }

    private static byte[] AppendHelperCbuffer(byte[] original, byte[] helper, bool merge)
    {
        var output = new List<byte>(original);
        var origin = output.Count;
        var bufferCount = checked((int)U32(helper, 0));
        var bufferAt = checked((int)U32(helper, 4));
        var bindingCount = checked((int)U32(helper, 8));
        var bindingAt = checked((int)U32(helper, 12));
        IasBinary.Require(bufferCount == 1 && bindingCount == 1, "Unsupported native material or shader layout");
        var buffer = new uint[6];
        for (var field = 0; field < buffer.Length; field++) buffer[field] = U32(helper, bufferAt + field * 4);
        var seenTypes = new HashSet<int>();
        for (var index = 0; index < buffer[1]; index++)
        {
            var at = checked((int)buffer[2]) + 40 * index;
            var row = new uint[10];
            for (var field = 0; field < row.Length; field++) row[field] = U32(helper, at + field * 4);
            var typeAt = checked((int)row[4]);
            if (seenTypes.Add(typeAt))
            {
                var kind = U16(helper, typeAt);
                var primitive = U16(helper, typeAt + 2);
                var elements = U16(helper, typeAt + 8);
                var members = U16(helper, typeAt + 10);
                IasBinary.Require(primitive == 3 && elements == 0 && members == 0 && kind is 0 or 1, "Unsupported native material or shader layout");
                for (var field = 0; field < 5; field++) IasBinary.Require(U32(helper, typeAt + 12 + field * 4) == 0, "Unsupported native material or shader layout");
                var nameAt = U32(helper, typeAt + 32);
                if (nameAt != 0) WriteU32(helper, typeAt + 32, checked(nameAt + (uint)origin));
            }
            for (var field = 0; field < row.Length; field++)
            {
                var adjustment = field is 0 or 4 || field == 5 && row[field] != 0 ? (uint)origin : 0;
                WriteU32(helper, at + field * 4, checked(row[field] + adjustment));
            }
        }
        for (var field = 0; field < buffer.Length; field++)
            WriteU32(helper, bufferAt + field * 4, checked(buffer[field] + (uint)(field is 0 or 2 ? origin : 0)));
        buffer[0] = checked(buffer[0] + (uint)origin);
        buffer[2] = checked(buffer[2] + (uint)origin);
        var resource = new uint[8];
        for (var field = 0; field < resource.Length; field++) resource[field] = U32(helper, bindingAt + field * 4);
        IasBinary.Require(resource[1] == 0, "Unsupported native material or shader layout");
        resource[0] = checked(resource[0] + (uint)origin);
        output.AddRange(helper);
        while (output.Count % 8 != 0) output.Add(0);
        var oldBufferCount = checked((int)U32(original, 0));
        var oldBufferAt = checked((int)U32(original, 4));
        var oldBindingCount = checked((int)U32(original, 8));
        var oldBindingAt = checked((int)U32(original, 12));
        var oldBuffers = new List<uint[]>(oldBufferCount);
        for (var index = 0; index < oldBufferCount; index++)
        {
            var row = new uint[6];
            for (var field = 0; field < row.Length; field++) row[field] = U32(original, oldBufferAt + index * 24 + field * 4);
            oldBuffers.Add(row);
        }
        if (merge)
        {
            var matches = oldBuffers.Select((row, index) => (row, index)).Where(item => CString(original, item.row[0]) == "c_per_object")
                .Select(item => item.index).ToList();
            IasBinary.Require(matches.Count == 1, "Unsupported native material or shader layout");
            var old = oldBuffers[matches[0]];
            var variablesAt = output.Count;
            output.AddRange(new ReadOnlySpan<byte>(original, checked((int)old[2]), checked((int)old[1]) * 40).ToArray());
            var helperFields = CollectionsMarshal.AsSpan(output)
                .Slice(checked((int)buffer[2]), checked((int)buffer[1]) * 40).ToArray();
            output.AddRange(helperFields);
            old[1] += buffer[1];
            old[2] = (uint)variablesAt;
            old[3] = Math.Max(old[3], buffer[3]);
        }
        else oldBuffers.Add(buffer);
        var newBufferAt = output.Count;
        foreach (var row in oldBuffers)
            foreach (var value in row) output.AddRange(BitConverter.GetBytes(value));
        var newBindingAt = output.Count;
        output.AddRange(new ReadOnlySpan<byte>(original, oldBindingAt, oldBindingCount * 32).ToArray());
        if (!merge) foreach (var value in resource) output.AddRange(BitConverter.GetBytes(value));
        var prefix = new byte[16];
        WriteU32(prefix, 0, checked((uint)(oldBufferCount + (merge ? 0 : 1))));
        WriteU32(prefix, 4, (uint)newBufferAt);
        WriteU32(prefix, 8, checked((uint)(oldBindingCount + (merge ? 0 : 1))));
        WriteU32(prefix, 12, (uint)newBindingAt);
        for (var index = 0; index < prefix.Length; index++) output[index] = prefix[index];
        return output.ToArray();
    }

    private static ObjectBuffer? ObjectReflection(byte[] code)
    {
        var raw = Chunk(code, "RDEF");
        var count = U32(raw, 0);
        var start = U32(raw, 4);
        var matches = new List<ObjectBuffer>();
        for (var index = 0; index < count; index++)
        {
            var at = start + index * 24;
            var name = U32(raw, at);
            if (CString(raw, name) != "c_per_object") continue;
            var variables = new List<ReflectedVariable>();
            for (var member = 0; member < U32(raw, at + 4); member++)
            {
                var item = checked((int)U32(raw, at + 8)) + member * 40;
                variables.Add(new ReflectedVariable(CString(raw, U32(raw, item)), (int)U32(raw, item + 4), (int)U32(raw, item + 8)));
            }
            matches.Add(new ObjectBuffer((int)U32(raw, at + 12), variables));
        }
        IasBinary.Require(matches.Count <= 1, "Duplicate c_per_object reflection");
        return matches.SingleOrDefault();
    }

    private sealed record ObjectBuffer(int Size, IReadOnlyList<ReflectedVariable> Variables);
    private sealed record ReflectedVariable(string Name, int Offset, int Size);
    private sealed record Binding(string Name, int Kind, int Slot);
    private sealed class SignatureRow
    {
        public required string Semantic { get; init; }
        public required int Index { get; init; }
        public required int System { get; init; }
        public required int ComponentType { get; init; }
        public required int Register { get; init; }
        public int Mask { get; set; }
        public int Used { get; set; }
    }
    private sealed record Instruction(string Line, uint[] Words);
    private sealed record TokenHeader(uint Version, uint Count);

    private static List<Binding> Bindings(byte[] code)
    {
        var data = Chunk(code, "RDEF");
        var count = U32(data, 8);
        var offset = U32(data, 12);
        var result = new List<Binding>();
        for (var index = 0; index < count; index++)
        {
            var at = offset + index * 32;
            result.Add(new Binding(CString(data, U32(data, at)), (int)U32(data, at + 4), (int)U32(data, at + 20)));
        }
        return result;
    }

    private static List<SignatureRow> Signature(byte[] code, string tag)
    {
        var payload = Chunk(code, tag);
        var count = U32(payload, 0);
        var result = new List<SignatureRow>();
        for (var index = 0; index < count; index++)
        {
            var at = 8 + index * 24;
            result.Add(new SignatureRow
            {
                Semantic = CString(payload, U32(payload, at)), Index = (int)U32(payload, at + 4),
                System = (int)U32(payload, at + 8), ComponentType = (int)U32(payload, at + 12),
                Register = (int)U32(payload, at + 16), Mask = payload[at + 20], Used = payload[at + 21]
            });
        }
        return result;
    }

    private static byte[] SignatureData(IReadOnlyList<SignatureRow> rows)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<byte>();
        var elements = new List<byte>();
        var offset = 8 + rows.Count * 24;
        foreach (var row in rows)
        {
            if (!names.TryGetValue(row.Semantic, out var nameOffset))
            {
                nameOffset = offset + strings.Count;
                names[row.Semantic] = nameOffset;
                strings.AddRange(Encoding.ASCII.GetBytes(row.Semantic));
                strings.Add(0);
            }
            var element = new byte[24];
            WriteU32(element, 0, (uint)nameOffset);
            WriteU32(element, 4, (uint)row.Index);
            WriteU32(element, 8, (uint)row.System);
            WriteU32(element, 12, 3);
            WriteU32(element, 16, (uint)row.Register);
            element[20] = (byte)row.Mask;
            element[21] = (byte)row.Used;
            elements.AddRange(element);
        }
        var result = new List<byte>();
        result.AddRange(BitConverter.GetBytes(rows.Count));
        result.AddRange(BitConverter.GetBytes(8U));
        result.AddRange(elements);
        result.AddRange(strings);
        while (result.Count % 4 != 0) result.Add(0);
        return result.ToArray();
    }

    private static (TokenHeader Header, List<Instruction> Rows) InstructionRows(byte[] data)
    {
        var parts = Chunks(data);
        var payload = parts.First(part => part.Tag is "SHDR" or "SHEX").Payload;
        var tokens = new uint[payload.Length / 4];
        for (var index = 0; index < tokens.Length; index++) tokens[index] = U32(payload, index * 4);
        IasBinary.Require(tokens.Length >= 2 && tokens[1] == tokens.Length, "Unsupported native material or shader layout");
        var raw = new List<uint[]>();
        var position = 2;
        while (position < tokens.Length)
        {
            var length = checked((int)((tokens[position] & 2047) == 53 ? tokens[position + 1] : tokens[position] >> 24 & 127));
            IasBinary.Require(length > 0 && position + length <= tokens.Length, "Unsupported native material or shader layout");
            raw.Add(tokens[position..(position + length)]);
            position += length;
        }
        var split = Stage.Split(Disassemble(data), 2);
        IasBinary.Require(split.Length == 2, "Unsupported native material or shader layout");
        var lines = new List<string>();
        var pending = new List<string>();
        var balance = 0;
        foreach (var sourceLine in split[1].Split('\n'))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (pending.Count > 0)
            {
                pending.Add(line);
                balance += line.Count(ch => ch == '{') - line.Count(ch => ch == '}');
                if (balance == 0)
                {
                    lines.Add(string.Join(' ', pending));
                    pending.Clear();
                }
            }
            else if (line.StartsWith("dcl_immediateConstantBuffer", StringComparison.Ordinal) && line.Count(ch => ch == '{') > line.Count(ch => ch == '}'))
            {
                pending.Add(line);
                balance = line.Count(ch => ch == '{') - line.Count(ch => ch == '}');
            }
            else lines.Add(line);
        }
        IasBinary.Require(pending.Count == 0 && lines.Count == raw.Count, "DXBC instruction/disassembly row count differs");
        return (new TokenHeader(tokens[0], tokens[1]), lines.Zip(raw).Select(pair => new Instruction(pair.First, pair.Second)).ToList());
    }

    private static uint[] Remap(uint[] source, int tempBase = 0,
        IReadOnlyDictionary<int, (int Kind, int Number)>? inputMap = null,
        IReadOnlyDictionary<int, (int Kind, int Number)>? outputMap = null,
        IReadOnlyDictionary<int, (int Kind, int Number)>? resourceMap = null)
    {
        var words = source.ToArray();
        inputMap ??= new Dictionary<int, (int, int)>();
        outputMap ??= new Dictionary<int, (int, int)>();
        resourceMap ??= new Dictionary<int, (int, int)>();
        var index = 1;
        var opcode = words[0];
        while ((opcode & 0x80000000) != 0) opcode = words[index++];
        int Operand(int position)
        {
            var start = position;
            var token = words[position++];
            var kind = token >> 12 & 255;
            var dimensions = token >> 20 & 3;
            var extended = token;
            while ((extended & 0x80000000) != 0) extended = words[position++];
            if (kind is 4 or 5)
            {
                var count = (token & 3) switch { 0 => 0, 1 => 1, 2 => 4, _ => -1 };
                IasBinary.Require(count >= 0, "未知立即数分量数量。");
                return position + count * (kind == 5 ? 2 : 1);
            }
            int? firstIndex = null;
            for (var dimension = 0; dimension < dimensions; dimension++)
            {
                var representation = token >> (22 + 3 * dimension) & 7;
                if (representation is 0 or 1 or 3 or 4)
                {
                    if (dimension == 0) firstIndex = position;
                    position += representation is 1 or 4 ? 2 : 1;
                }
                if (representation is 2 or 3 or 4) position = Operand(position);
                else if (representation is not (0 or 1)) throw new InvalidDataException("未知寄存器索引。");
            }
            if (kind is 0 or 1 or 2 or 7 && firstIndex is int registerIndex)
            {
                var number = words[registerIndex];
                (int Kind, int Number)? replacement = kind switch
                {
                    0 => (0, (int)number + tempBase),
                    1 when inputMap.ContainsKey((int)number) => inputMap[(int)number],
                    2 when outputMap.ContainsKey((int)number) => outputMap[(int)number],
                    7 when resourceMap.ContainsKey((int)number) => resourceMap[(int)number],
                    _ => null
                };
                if (replacement is { } mapped)
                {
                    words[start] = (uint)((token & unchecked((int)0xFFF00FFF)) | (mapped.Kind << 12));
                    words[registerIndex] = (uint)mapped.Number;
                }
            }
            return position;
        }
        while (index < words.Length) index = Operand(index);
        IasBinary.Require(index == words.Length, "操作数解析未恰好结束。");
        return words;
    }

    private static List<(string Tag, byte[] Payload)> Chunks(byte[] data)
    {
        IasBinary.Require(data.Length >= 32 && new ReadOnlySpan<byte>(data, 0, 4).SequenceEqual("DXBC"u8), "不是 DXBC 容器。");
        var size = U32(data, 24);
        var count = U32(data, 28);
        IasBinary.Require(size <= data.Length && count <= 64, "DXBC 容器越界。");
        var result = new List<(string, byte[])>();
        for (var index = 0; index < count; index++)
        {
            var offset = checked((int)U32(data, 32 + index * 4));
            IasBinary.Require(offset + 8 <= data.Length, "DXBC Chunk 越界。");
            var length = checked((int)U32(data, offset + 4));
            IasBinary.Require(offset + 8L + length <= size, "DXBC Chunk 越界。");
            result.Add((Encoding.ASCII.GetString(data, offset, 4), new ReadOnlySpan<byte>(data, offset + 8, length).ToArray()));
        }
        return result;
    }

    private static byte[] Chunk(byte[] data, string tag)
        => Chunks(data).First(part => part.Tag == tag).Payload;

    private static byte[] BuildContainer(IReadOnlyList<(string Tag, byte[] Payload)> parts, int? targetSize = null)
    {
        var size = 32 + parts.Count * 4 + parts.Sum(part => 8 + part.Payload.Length);
        var result = new List<(string Tag, byte[] Payload)>(parts);
        if (targetSize is { } target)
        {
            var gap = target - size;
            IasBinary.Require(gap >= 0, $"着色器空间不足：需要 {size}，槽容量 {target}。");
            if (gap >= 12)
            {
                var marker = "LHD2_NATIVE_SHADER_02"u8.ToArray();
                var payload = gap - 12 >= marker.Length
                    ? marker.Concat(new byte[gap - 12 - marker.Length]).ToArray()
                    : new byte[gap - 12];
                result.Add(("PRIV", payload));
                size = target;
            }
            else IasBinary.Require(gap == 0, $"无法生成合法填充：剩余 {gap} 字节。");
        }
        var data = new byte[size];
        "DXBC"u8.CopyTo(data);
        WriteU32(data, 20, 1);
        WriteU32(data, 24, (uint)size);
        WriteU32(data, 28, (uint)result.Count);
        var position = 32 + result.Count * 4;
        for (var index = 0; index < result.Count; index++)
        {
            WriteU32(data, 32 + index * 4, (uint)position);
            Encoding.ASCII.GetBytes(result[index].Tag).CopyTo(data, position);
            WriteU32(data, position + 4, (uint)result[index].Payload.Length);
            result[index].Payload.CopyTo(data, position + 8);
            position += 8 + result[index].Payload.Length;
        }
        Checksum(data).CopyTo(data, 4);
        return data;
    }

    private static byte[] Checksum(byte[] data)
    {
        var payload = new ReadOnlySpan<byte>(data, 20, data.Length - 20);
        var bits = (ulong)payload.Length * 8;
        var full = payload.Length / 64 * 64;
        var blocks = new List<byte[]>();
        for (var index = 0; index < full; index += 64) blocks.Add(payload.Slice(index, 64).ToArray());
        var tail = payload[full..].ToArray();
        if (tail.Length >= 56)
        {
            blocks.Add(tail.Concat(new byte[] { 0x80 }).Concat(new byte[63 - tail.Length]).ToArray());
            var last = new byte[64];
            WriteU32(last, 0, (uint)bits);
            WriteU32(last, 60, (uint)(bits >> 2) | 1U);
            blocks.Add(last);
        }
        else
        {
            var block = new byte[64];
            WriteU32(block, 0, (uint)bits);
            tail.CopyTo(block, 4);
            block[4 + tail.Length] = 0x80;
            WriteU32(block, 60, (uint)(bits >> 2) | 1U);
            blocks.Add(block);
        }
        uint[] state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];
        foreach (var block in blocks) state = Md5Block(state, block);
        var result = new byte[16];
        for (var index = 0; index < 4; index++) WriteU32(result, index * 4, state[index]);
        return result;
    }

    private static uint[] Md5Block(uint[] state, byte[] block)
    {
        var words = new uint[16];
        for (var index = 0; index < 16; index++) words[index] = U32(block, index * 4);
        var shifts = new[]
        {
            7,12,17,22,7,12,17,22,7,12,17,22,7,12,17,22,
            5,9,14,20,5,9,14,20,5,9,14,20,5,9,14,20,
            4,11,16,23,4,11,16,23,4,11,16,23,4,11,16,23,
            6,10,15,21,6,10,15,21,6,10,15,21,6,10,15,21
        };
        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (var index = 0; index < 64; index++)
        {
            uint f;
            int g;
            if (index < 16) { f = (b & c) | (~b & d); g = index; }
            else if (index < 32) { f = (d & b) | (~d & c); g = (5 * index + 1) % 16; }
            else if (index < 48) { f = b ^ c ^ d; g = (3 * index + 5) % 16; }
            else { f = c ^ (b | ~d); g = 7 * index % 16; }
            var value = unchecked(a + f + (uint)(Math.Abs(Math.Sin(index + 1)) * 4294967296.0) + words[g]);
            value = (value << shifts[index]) | (value >> (32 - shifts[index]));
            a = d; d = c; c = b; b = unchecked(b + value);
        }
        return [unchecked(state[0] + a), unchecked(state[1] + b), unchecked(state[2] + c), unchecked(state[3] + d)];
    }

    private static byte[] BlobBytes(IntPtr pointer)
    {
        var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(pointer);
        try
        {
            var address = blob.GetBufferPointer();
            var size = checked((int)blob.GetBufferSize());
            var result = new byte[size];
            Marshal.Copy(address, result, 0, size);
            return result;
        }
        finally { Marshal.ReleaseComObject(blob); }
    }

    private static void ReleaseBlob(IntPtr pointer) => ReleaseCom(pointer);

    private static void ReleaseCom(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return;
        var unknown = (IUnknown)Marshal.GetObjectForIUnknown(pointer);
        try { _ = unknown.Release(); }
        finally { Marshal.ReleaseComObject(unknown); }
    }

    [ComImport, Guid("00000000-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUnknown
    {
        [PreserveSig] int QueryInterface(ref Guid iid, out IntPtr result);
        [PreserveSig] uint AddRef();
        [PreserveSig] uint Release();
    }

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] UIntPtr GetBufferSize();
    }

    [ComImport, Guid("DB6F6DDB-AC77-4E88-8253-819DF9BBF140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device
    {
        [PreserveSig] int CreateBuffer(IntPtr descriptor, IntPtr initialData, IntPtr result);
        [PreserveSig] int CreateTexture1D(IntPtr descriptor, IntPtr initialData, IntPtr result);
        [PreserveSig] int CreateTexture2D(IntPtr descriptor, IntPtr initialData, IntPtr result);
        [PreserveSig] int CreateTexture3D(IntPtr descriptor, IntPtr initialData, IntPtr result);
        [PreserveSig] int CreateShaderResourceView(IntPtr resource, IntPtr descriptor, IntPtr result);
        [PreserveSig] int CreateUnorderedAccessView(IntPtr resource, IntPtr descriptor, IntPtr result);
        [PreserveSig] int CreateRenderTargetView(IntPtr resource, IntPtr descriptor, IntPtr result);
        [PreserveSig] int CreateDepthStencilView(IntPtr resource, IntPtr descriptor, IntPtr result);
        [PreserveSig] int CreateInputLayout(IntPtr elements, uint count, IntPtr code, UIntPtr length, IntPtr result);
        [PreserveSig] int CreateVertexShader(IntPtr code, UIntPtr length, IntPtr linkage, out IntPtr result);
        [PreserveSig] int CreateGeometryShader(IntPtr code, UIntPtr length, IntPtr linkage, IntPtr result);
        [PreserveSig] int CreateGeometryShaderWithStreamOutput(IntPtr code, UIntPtr length, IntPtr linkage, IntPtr elements, uint count, IntPtr result);
        [PreserveSig] int CreatePixelShader(IntPtr code, UIntPtr length, IntPtr linkage, out IntPtr result);
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static uint U32(ReadOnlySpan<byte> data, uint offset) => U32(data, checked((int)offset));
    private static uint U32(ReadOnlySpan<byte> data, long offset) => U32(data, checked((int)offset));
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    private static void WriteU32(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, 4), value);
    private static void WriteU32(Span<byte> data, uint offset, uint value) => WriteU32(data, checked((int)offset), value);
    private static void WriteU32(Span<byte> data, long offset, uint value) => WriteU32(data, checked((int)offset), value);
    private static void WriteU32(byte[] data, int offset, uint value) => WriteU32(new Span<byte>(data), offset, value);
    private static string CString(ReadOnlySpan<byte> data, uint offset)
    {
        var start = checked((int)offset);
        IasBinary.Require(start >= 0 && start < data.Length, "Native string offset out of range");
        var end = data[start..].IndexOf((byte)0);
        IasBinary.Require(end >= 0, "Native string is not terminated");
        return Encoding.ASCII.GetString(data.Slice(start, end));
    }
}
