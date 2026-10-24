using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

public static class IndependentLuaSourceBuilder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Build(byte[] runtime, byte[] loader, Hd2IASRuntimeManifest manifest)
    {
        var body = Normalize(Utf8.GetString(runtime)).TrimEnd();
        if (Hash(Utf8.GetBytes(body)) != manifest.IndependentLua.BodySha256)
            throw new InvalidDataException("Normalized HD2IAS runtime body hash does not match.");
        var template = Normalize(Utf8.GetString(loader)).TrimEnd() + "\n";
        foreach (var marker in new[] { "@@MODULE_IDENTITY@@", "@@RUNTIME_BODY@@" })
            if (template.Split(marker, StringSplitOptions.None).Length != 2)
                throw new InvalidDataException($"HD2IAS template requires exactly one {marker}.");
        var source = Utf8.GetBytes(template.Replace("@@MODULE_IDENTITY@@", manifest.IndependentLua.Identity, StringComparison.Ordinal)
            .Replace("@@RUNTIME_BODY@@", body, StringComparison.Ordinal));
        // 身份来自运行包清单；最终源码哈希同时约束模板、身份替换和正文，拒绝自行猜测另一套身份算法。
        if (Hash(source) != manifest.IndependentLua.SourceSha256)
            throw new InvalidDataException("Generated HD2IAS Lua source hash does not match its declared identity.");
        return source;
    }

    private static string Normalize(string value) => value.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
    internal static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
