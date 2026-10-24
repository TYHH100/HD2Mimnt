using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Services;

public sealed record ModReplacementResult(Mod Mod, bool DeploymentWasCleaned, string? CleanupWarning);
