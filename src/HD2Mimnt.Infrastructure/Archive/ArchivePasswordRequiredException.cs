namespace HD2Mimnt.Infrastructure.Archive;

/// <summary>压缩包需要密码，或提供的密码未能解密。</summary>
public sealed class ArchivePasswordRequiredException() : IOException("ArchivePasswords.Required");
