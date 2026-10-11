namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Represents a file associated with a platform Mod.
/// </summary>
public class PlatformModFile
{
    /// <summary>
    /// Gets or sets the file ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>网站文件编号，与平台全局文件版本 ID 分开保存。</summary>
    public string? WebsiteFileId { get; set; }

    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>平台提供的原始压缩包文件名；不存在时由 UI 给出默认名称。</summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the download URL.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the version this file belongs to.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this file was uploaded.
    /// </summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>
    /// Gets or sets the file description.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
