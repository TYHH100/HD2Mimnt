using HD2Mimnt.Core.Darktide;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public sealed class DarktideDiagnosticItemViewModel
{
    public DarktideDiagnosticItemViewModel(DarktideDiagnostic diagnostic)
    {
        Severity = diagnostic.Severity;
        IsError = diagnostic.Severity == DarktideIssueSeverity.Error;
        IsWarning = diagnostic.Severity == DarktideIssueSeverity.Warning;
        var title = UiLocalization.Current[$"Darktide.Issue.{diagnostic.Kind}"];
        if (title.StartsWith("Darktide.Issue.", StringComparison.Ordinal)) title = diagnostic.Kind.ToString();
        var subject = diagnostic.ModName ?? "";
        if (!string.IsNullOrEmpty(diagnostic.RelatedName)) subject += $" → {diagnostic.RelatedName}";
        Title = string.IsNullOrEmpty(subject) ? title : $"{title}：{subject}";
        Detail = diagnostic.Detail ?? "";
    }

    public DarktideIssueSeverity Severity { get; }
    public bool IsError { get; }
    public bool IsWarning { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
}

public sealed class DarktideModItemViewModel
{
    public DarktideModItemViewModel(DarktideModOverview mod)
    {
        Model = mod;
        Diagnostics = mod.Diagnostics.Select(item => new DarktideDiagnosticItemViewModel(item)).ToList();
    }

    public DarktideModOverview Model { get; }
    public string InternalName => Model.InternalName;
    public string DisplayName => Model.DisplayName;
    public string Version => string.IsNullOrWhiteSpace(Model.Version) ? "—" : Model.Version;
    public string Author => string.IsNullOrWhiteSpace(Model.Author) ? "—" : Model.Author;
    public string Description => Model.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool InLibrary => Model.InLibrary;
    public bool Installed => Model.Installed;
    public bool Registered => Model.Registered;
    public bool IsMissing => !Model.Installed && Model.Registered;
    public bool HasIssues => Diagnostics.Count > 0;
    public IReadOnlyList<DarktideDiagnosticItemViewModel> Diagnostics { get; }
    public string ArchiveSizeText => Model.ArchiveSize > 0 ? FormatSize(Model.ArchiveSize) : "";
    public bool HasArchive => Model.ArchiveSize > 0;
    public bool CanInstall => InLibrary && !Installed;
    public bool CanReinstall => InLibrary && Installed;
    public bool CanUninstall => Installed;
    public bool CanRemoveLibrary => InLibrary && !Installed;
    public bool CanToggle => Installed || Registered;
    public bool IsRegisteredOff => Installed && !Registered;
    public bool CanOpenFolder => Installed && Model.InstalledPath != null;

    // 分组色板只影响卡片背景，成员每次刷新重新计算，不需要通知。
    public bool IsInsideGroup { get; set; }
    public bool IsSlabStart { get; set; }
    public bool IsSlabEnd { get; set; }
    public Avalonia.Media.IBrush? GroupTintBrush { get; set; }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0} KB";
}

/// <summary>库列表里的用户分组行。禁用模组保持原位，因此组内成员与位点一样稳定。</summary>
public sealed class DarktideSeparatorRowViewModel
{
    public DarktideSeparatorRowViewModel(DarktideModSeparator model)
    {
        Model = model;
        Title = model.Name;
        HasCustomColor = !string.IsNullOrEmpty(model.Color);
    }

    public DarktideModSeparator Model { get; }
    public Guid Id => Model.Id;
    public string Title { get; }
    public bool IsCollapsed => Model.IsCollapsed;
    public bool HasCustomColor { get; }
    public Avalonia.Media.IBrush? TintBrush { get; set; }

    /// <summary>组内成员数；折叠时用它说明隐藏了多少。</summary>
    public int MemberCount { get; set; }
    public bool HasMembers => MemberCount > 0;
    public string MemberSummary => MemberCount.ToString();
    public double ChevronAngle => IsCollapsed ? 0 : 90;

    /// <summary>组在稳定序列中的起止位点，用于“在本组上方/下方添加分组”。</summary>
    public int StartOrder { get; set; }
    public int EndOrder { get; set; }
}

public sealed class DarktideLoadOrderItemViewModel
{
    public DarktideLoadOrderItemViewModel(string internalName, DarktideModOverview? mod, int position, bool registered)
    {
        InternalName = internalName;
        DisplayName = mod?.DisplayName ?? internalName;
        Position = position + 1;
        Registered = registered;
        Installed = mod?.Installed ?? false;
        IsFramework = DarktidePaths.IsFrameworkFolder(internalName);
        Missing = registered && !Installed && !IsFramework;
        Version = mod?.Version ?? "";
    }

    public string InternalName { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public int Position { get; }
    public bool Registered { get; }
    public bool Installed { get; }
    public bool Missing { get; }
    public bool IsFramework { get; }
    public bool CanMove => Registered && !IsFramework;
    public bool CanToggle => Registered;
    public bool ShowEnable => !Registered;
}

public sealed class DarktideProfileItemViewModel
{
    public DarktideProfileItemViewModel(DarktideProfile profile)
    {
        Profile = profile;
        Name = profile.Name;
        IsActive = profile.IsActive;
        EnabledCount = profile.Entries.Count(entry => entry.Enabled);
        TotalCount = profile.Entries.Count;
        UpdatedText = profile.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public DarktideProfile Profile { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public int EnabledCount { get; }
    public int TotalCount { get; }
    public string Summary => $"{EnabledCount}/{TotalCount}";
    public string UpdatedText { get; }
}
