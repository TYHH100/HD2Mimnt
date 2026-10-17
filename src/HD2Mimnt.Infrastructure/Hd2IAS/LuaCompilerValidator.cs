using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>使用游戏自带、哈希匹配的 VM 做编译检查。只 loadbuffer，不执行系统 Lua。</summary>
public sealed class LuaCompilerValidator : IHd2IASLuaCompiler
{
    public async Task ValidateAsync(string lua51Path, string expectedSha256, byte[] source, string module,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("HD2IAS Lua validation requires Windows x64.");
        await using (var stream = File.OpenRead(lua51Path))
            if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Game lua51.dll SHA-256 does not match the HD2IAS package.");
        cancellationToken.ThrowIfCancellationRequested();
        var library = NativeLibrary.Load(Path.GetFullPath(lua51Path));
        try
        {
            var create = Marshal.GetDelegateForFunctionPointer<NewState>(NativeLibrary.GetExport(library, "luaL_newstate"));
            var close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "lua_close"));
            var load = Marshal.GetDelegateForFunctionPointer<LoadBuffer>(NativeLibrary.GetExport(library, "luaL_loadbuffer"));
            var errorText = Marshal.GetDelegateForFunctionPointer<LuaToString>(NativeLibrary.GetExport(library, "lua_tolstring"));
            var state = create();
            if (state == 0) throw new InvalidOperationException("luaL_newstate failed.");
            try
            {
                if (load(state, source, (nuint)source.Length, "@" + module) != 0)
                {
                    var pointer = errorText(state, -1, out var length);
                    throw new InvalidDataException("Lua compilation failed: " + (pointer == 0 ? "unknown error" : Marshal.PtrToStringUTF8(pointer, checked((int)length))));
                }
            }
            finally { close(state); }
        }
        finally { NativeLibrary.Free(library); }
        cancellationToken.ThrowIfCancellationRequested();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NewState();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Close(nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadBuffer(nint state, byte[] buffer, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint LuaToString(nint state, int index, out nuint length);
}
