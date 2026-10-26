namespace HD2Mimnt.Core.Entities;

/// <summary>三个可独立配置的存储根目录；派生目录不另存设置。</summary>
public sealed record StorageLocations(string AppDataPath, string ModsPath, string CachePath)
{
    public string DatabasePath => System.IO.Path.Combine(AppDataPath, "darktidemimnt.db");
    public string ImageCachePath => System.IO.Path.Combine(CachePath, "ImageCache");
    public string DownloadsPath => System.IO.Path.Combine(CachePath, "Downloads");
    public string TempPath => System.IO.Path.Combine(CachePath, "Temp");
}
