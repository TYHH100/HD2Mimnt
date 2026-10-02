using BenchmarkDotNet.Attributes;
using HD2Mimnt.Infrastructure.Services;
using System.Drawing;
using System.Drawing.Imaging;

namespace HD2Mimnt.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class ImageBenchmarks
{
    private ImageCacheService? _imageCacheService;
    private string? _testImagePath;
    private string? _largeImagePath;
    private string? _mediumImagePath;
    private string? _smallImagePath;
    private string? _cacheDir;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"benchmark_images_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        // Create test images of different sizes
        _smallImagePath = Path.Combine(tempDir, "small.png");
        _mediumImagePath = Path.Combine(tempDir, "medium.png");
        _largeImagePath = Path.Combine(tempDir, "large.png");
        _testImagePath = _mediumImagePath;

        CreateTestImage(_smallImagePath, 256, 256);
        CreateTestImage(_mediumImagePath, 1024, 1024);
        CreateTestImage(_largeImagePath, 4096, 4096);

        _cacheDir = Path.Combine(Path.GetTempPath(), $"benchmark_cache_{Guid.NewGuid()}");
        Directory.CreateDirectory(_cacheDir);

        _imageCacheService = new ImageCacheService(_cacheDir, 100);
    }

    private void CreateTestImage(string path, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);

        // Fill with gradient for more realistic image
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int r = (x * 255) / width;
                int g = (y * 255) / height;
                int b = ((x + y) * 255) / (width + height);
                bitmap.SetPixel(x, y, Color.FromArgb(r, g, b));
            }
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        var tempDir = Path.GetDirectoryName(_testImagePath);
        if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }

        if (!string.IsNullOrEmpty(_cacheDir) && Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, true);
        }
    }

    [Benchmark]
    public void GenerateThumbnail_SmallImage()
    {
        var outputPath = Path.Combine(_cacheDir!, $"thumb_small_{Guid.NewGuid()}.png");
        CreateThumbnail(_smallImagePath!, outputPath, 256, 256);
    }

    [Benchmark]
    public void GenerateThumbnail_MediumImage()
    {
        var outputPath = Path.Combine(_cacheDir!, $"thumb_medium_{Guid.NewGuid()}.png");
        CreateThumbnail(_mediumImagePath!, outputPath, 256, 256);
    }

    [Benchmark]
    public void GenerateThumbnail_LargeImage()
    {
        var outputPath = Path.Combine(_cacheDir!, $"thumb_large_{Guid.NewGuid()}.png");
        CreateThumbnail(_largeImagePath!, outputPath, 256, 256);
    }

    private void CreateThumbnail(string sourcePath, string outputPath, int width, int height)
    {
        using var sourceImage = Image.FromFile(sourcePath);
        using var thumbnail = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(thumbnail);

        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

        graphics.DrawImage(sourceImage, 0, 0, width, height);
        thumbnail.Save(outputPath, ImageFormat.Png);
    }

    [Benchmark]
    public void LoadImageFromDisk()
    {
        using var stream = File.OpenRead(_testImagePath!);
        using var image = Image.FromStream(stream);
        // Simulate processing
        var width = image.Width;
        var height = image.Height;
    }

    [Benchmark]
    [Arguments(10)]
    [Arguments(50)]
    public void BatchLoadImages(int count)
    {
        var images = new[] { _smallImagePath!, _mediumImagePath!, _largeImagePath! };

        for (int i = 0; i < count; i++)
        {
            var imagePath = images[i % images.Length];
            using var stream = File.OpenRead(imagePath);
            using var image = Image.FromStream(stream);
            var width = image.Width;
            var height = image.Height;
        }
    }
}
