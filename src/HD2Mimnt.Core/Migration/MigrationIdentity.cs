using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.Core.Migration;

/// <summary>只复用相同来源、相同内容的迁移副本，不依据作者 GUID 或名称匹配用户记录。</summary>
public static class MigrationIdentity
{
    public static Guid Create(params string[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return new Guid(hash.GetHashAndReset().AsSpan(0, 16));
    }

    public static string Source(ExternalMigrationPlan plan)
        => $"external-migration-v1:{plan.Source.Manager}:{Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.SourceIdentity)).ToUpperInvariant()}";
}
