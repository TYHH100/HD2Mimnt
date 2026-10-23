using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Drawing;
using System.Drawing.Imaging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Thumbnail Service
/// 负责生成和管理 Mod 缩略图
/// </summary>
public class ThumbnailService
{
    private readonly string _thumbnailsPath;
    private readonly ILogger<ThumbnailService>? _logger;
    private const int ThumbnailWidth = 256;
    private const int ThumbnailHeight = 256;

    public ThumbnailService(string thumbnailsPath, ILogger<ThumbnailService>? logger = null)
    {
        _thumbnailsPath = thumbnailsPath;
        _logger = logger;
        Directory.CreateDirectory(_thumbnailsPath);
    }

    /// <summary>
    /// 为 Mod 生成缩略图
    /// </summary>
    public async Task<string?> GenerateThumbnailAsync(Guid modId, string modPath, CancellationToken cancellationToken = default,
        string? iconPath = null)
    {
        try
        {
            var thumbnailFile = ModImportService.ResolveCardPreviewPath(modPath, iconPath);

            if (thumbnailFile == null)
            {
                _logger?.LogInformation("未找到 Mod 图片文件: {ModPath}", modPath);
                return null;
            }

            // 生成缩略图
            var thumbnailPath = Path.Combine(_thumbnailsPath, $"{modId}.png");
            await Task.Run(() => CreateThumbnail(thumbnailFile, thumbnailPath), cancellationToken);

            _logger?.LogInformation("缩略图生成成功: {ThumbnailPath}", thumbnailPath);
            return thumbnailPath;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "生成缩略图失败: ModId={ModId}, ModPath={ModPath}", modId, modPath);
            return null;
        }
    }

    /// <summary>
    /// 从指定图片创建缩略图
    /// </summary>
    public async Task<string?> CreateThumbnailFromImageAsync(Guid modId, string imagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException($"图片文件不存在: {imagePath}");
            }

            var thumbnailPath = Path.Combine(_thumbnailsPath, $"{modId}.png");
            await Task.Run(() => CreateThumbnail(imagePath, thumbnailPath), cancellationToken);

            _logger?.LogInformation("自定义缩略图创建成功: {ThumbnailPath}", thumbnailPath);
            return thumbnailPath;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "创建自定义缩略图失败: ModId={ModId}, ImagePath={ImagePath}", modId, imagePath);
            return null;
        }
    }

    /// <summary>
    /// 获取缩略图路径
    /// </summary>
    public string? GetThumbnailPath(Guid modId)
    {
        var thumbnailPath = Path.Combine(_thumbnailsPath, $"{modId}.png");
        return File.Exists(thumbnailPath) ? thumbnailPath : null;
    }

    /// <summary>仅清理本服务目录内的缩略图，支持更新后使用独立文件名。</summary>
    public Task DeleteThumbnailFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return Task.CompletedTask;
        var fullPath = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_thumbnailsPath)) + Path.DirectorySeparatorChar;
        if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) File.Delete(fullPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 删除缩略图
    /// </summary>
    public async Task<bool> DeleteThumbnailAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        try
        {
            var thumbnailPath = Path.Combine(_thumbnailsPath, $"{modId}.png");
            if (File.Exists(thumbnailPath))
            {
                await Task.Run(() => File.Delete(thumbnailPath), cancellationToken);
                _logger?.LogInformation("缩略图已删除: {ThumbnailPath}", thumbnailPath);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "删除缩略图失败: ModId={ModId}", modId);
            return false;
        }
    }

    /// <summary>
    /// 创建缩略图（实际图像处理）
    /// </summary>
    private void CreateThumbnail(string sourcePath, string targetPath)
    {
        using var sourceImage = Image.FromFile(sourcePath);

        // 计算缩略图尺寸（保持宽高比）
        int width = ThumbnailWidth;
        int height = ThumbnailHeight;

        var aspectRatio = (double)sourceImage.Width / sourceImage.Height;
        if (aspectRatio > 1)
        {
            height = (int)(width / aspectRatio);
        }
        else
        {
            width = (int)(height * aspectRatio);
        }

        // 创建缩略图
        using var thumbnail = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(thumbnail);

        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

        graphics.DrawImage(sourceImage, 0, 0, width, height);

        // 保存为 PNG
        thumbnail.Save(targetPath, ImageFormat.Png);
    }
}
