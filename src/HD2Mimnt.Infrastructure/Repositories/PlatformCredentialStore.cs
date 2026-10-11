using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Infrastructure.Repositories;

public sealed class PlatformCredentialStore(HD2MimntDbContext database) : IPlatformCredentialStore
{
    private static byte[] Entropy(string platformId) => Encoding.UTF8.GetBytes("HD2Mimnt.PlatformCredentials.v1:" + platformId);

    public async Task SaveApiKeyAsync(string platformId, string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥加密保存需要 Windows。");
        var plaintext = Encoding.UTF8.GetBytes(apiKey);
        byte[] ciphertext;
        try { ciphertext = ProtectedData.Protect(plaintext, Entropy(platformId), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var connection = await database.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO PlatformCredentials (PlatformId, ProtectedApiKey) VALUES (@Platform, @Key) " +
            "ON CONFLICT(PlatformId) DO UPDATE SET ProtectedApiKey = excluded.ProtectedApiKey";
        command.Parameters.AddWithValue("@Platform", platformId);
        command.Parameters.Add("@Key", SqliteType.Blob).Value = ciphertext;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> ReadApiKeyAsync(string platformId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformId);
        var connection = await database.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProtectedApiKey FROM PlatformCredentials WHERE PlatformId = @Platform";
        command.Parameters.AddWithValue("@Platform", platformId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null || result is DBNull) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥解密需要 Windows。");
        var plaintext = ProtectedData.Unprotect((byte[])result, Entropy(platformId), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task DeleteApiKeyAsync(string platformId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformId);
        var connection = await database.GetConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlatformCredentials WHERE PlatformId = @Platform";
        command.Parameters.AddWithValue("@Platform", platformId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
