using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface ICompatibilityResourceNames
{
    Task<CompatibilityResourceNames> GetAsync(bool refresh = false, CancellationToken cancellationToken = default);
}
