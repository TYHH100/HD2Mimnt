using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface IDownloadOptionsProvider
{
    Task<DownloadOptions> GetOptionsAsync();
}
