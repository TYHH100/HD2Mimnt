using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public enum GpuUsageScope { Library, ProfileMod, ProfileTotal }

/// <summary>保持数值和展示分离，LocalizedBinding 在语言切换时重新生成文字。</summary>
public sealed record GpuUsageDisplay(ModGpuUsage? Usage, GpuUsageScope Scope,
    GpuResourceBytes? EffectiveBytes = null, bool Enabled = true, bool EffectiveComplete = true, string? Error = null)
{
    public GpuUsageText Label => new(this, false);
    public GpuUsageText Tooltip => new(this, true);
}

public sealed record GpuUsageText(GpuUsageDisplay Display, bool IsTooltip)
{
    public string Localize()
    {
        var loc = UiLocalization.Current;
        var usage = Display.Usage;
        var label = Display.Error != null ? loc["GpuUsage.Unknown"]
            : usage == null ? loc["GpuUsage.Calculating"]
            : !usage.IsComplete ? loc[usage.ResourceCount == 0 ? "GpuUsage.Unknown" : "GpuUsage.Partial"]
            : loc.Format("GpuUsage.Value", Format(usage.Bytes.TotalBytes));
        if (!IsTooltip) return label;
        var lines = new List<string>
        {
            loc[Display.Scope switch
            {
                GpuUsageScope.Library => "GpuUsage.LibraryScope",
                GpuUsageScope.ProfileMod => "GpuUsage.ProfileScope",
                _ => "GpuUsage.TotalScope"
            }],
            loc["GpuUsage.Explanation"]
        };
        if (Display.Error != null) lines.Add(Display.Error);
        else if (usage == null) lines.Add(label);
        else
        {
            lines.Add(loc.Format(usage.IsComplete ? "GpuUsage.Details" : "GpuUsage.PartialDetails",
                Format(usage.Bytes.TextureBytes), Format(usage.Bytes.ModelBytes), Format(usage.Bytes.OtherBytes),
                usage.ResourceCount));
            if (usage.IsComplete) lines.Add(loc.Format("GpuUsage.ExactTotal", usage.Bytes.TotalBytes.ToString("N0", loc.Culture)));
            if (Display.Scope == GpuUsageScope.ProfileMod)
                lines.Add(!Display.Enabled ? loc["GpuUsage.Disabled"]
                    : Display.EffectiveComplete && Display.EffectiveBytes is { } effective
                        ? loc.Format("GpuUsage.Effective", Format(effective.TotalBytes))
                        : loc["GpuUsage.EffectiveUnknown"]);
            lines.AddRange(usage.Warnings.Take(5));
            if (usage.Warnings.Count > 5) lines.Add(loc.Format("GpuUsage.MoreWarnings", usage.Warnings.Count - 5));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string Format(long bytes) => (bytes / 1048576d).ToString("N2", UiLocalization.Current.Culture) + " MiB";
}
