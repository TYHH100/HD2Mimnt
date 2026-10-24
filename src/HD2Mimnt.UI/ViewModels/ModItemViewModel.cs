using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Resources;
using HD2Mimnt.Core.Services;
using System.Diagnostics;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 分类枚举的中文显示名（列表胶囊、分类下拉、详情面板共用，避免直接显示英文名）。
/// </summary>
public static class ModCategoryText
{
    public static string ToDisplay(ModCategory category) => category switch
    {
        ModCategory.Armor => "护甲",
        ModCategory.Weapon => "武器",
        ModCategory.Character => "角色",
        ModCategory.Audio => "音频",
        ModCategory.UI => "界面",
        ModCategory.Gameplay => "玩法",
        ModCategory.Visual => "视觉",
        ModCategory.Script => "脚本",
        ModCategory.Mixed => "混合",
        ModCategory.Other => "其他",
        _ => "未分类"
    };
}

/// <summary>
/// 模组列表项 ViewModel
/// 用于模组库列表显示，包含启用状态和选择状态
/// </summary>
public partial class ModItemViewModel : ObservableObject
{
    private Mod _mod;
    public ModCompatibilityBadgeViewModel? CompatibilityBadge { get; set; }
    public System.Windows.Input.ICommand? CheckCompatibilityCommand { get; set; }
    [ObservableProperty] private ModVersionsViewModel? _localVersions;

    public void ApplyContent(Mod saved)
    {
        _mod = saved;
        IsDeployed = false;
        OnPropertyChanged(string.Empty);
    }

    // 基础信息
    public Guid Id => _mod.Id;
    public string Name => _mod.Name;
    public string Version => PlatformLink?.InstalledVersion ?? _mod.Version;
    public string Author => PlatformLink?.Author ?? _mod.Author;
    public string Description => _mod.Description;
    public bool IsHd2Ir => _mod.WorkflowType == "HD2IAS";
    public IReadOnlyList<string> Hd2IrComponents => _mod.LogicalMod?.Components
        .Select(component => $"{component.Name} · {component.DeployableFiles.Count}").ToArray() ?? [];
    public ModPlatformLink? PlatformLink => _mod.PlatformLink;
    public bool HasPlatformLink => PlatformLink != null;
    public bool CanCheckUpdates => PlatformLink?.SupportsUpdates == true;
    public bool HasAuthorPage => !string.IsNullOrWhiteSpace(AuthorPageUrl);
    public string SourceName => PlatformLink?.PlatformId switch
    { "ayakamods" => "Ayaka Mods", "nexusmods" => "Nexus Mods", _ => "作者主页" };
    public string AuthorPageUrl => AuthorWebsite.Normalize(PlatformLink?.AuthorUrl)
        ?? AuthorWebsite.Normalize(_mod.Manifest?.AuthorUrl) ?? string.Empty;
    public string PlatformLinkSummary => UiLocalization.Current.Translate(PlatformLink);
    public string PlatformLinkUrl => PlatformLink?.Url ?? string.Empty;

    public void ApplyPlatformLink(Mod saved)
    {
        _mod.PlatformLink = saved.PlatformLink;
        _mod.PlatformId = saved.PlatformId;
        _mod.PlatformModId = saved.PlatformModId;
        _mod.PlatformVersion = saved.PlatformVersion;
        _mod.NexusModId = saved.NexusModId;
        _mod.LastUpdateCheck = saved.LastUpdateCheck;
        _mod.ModifiedAt = saved.ModifiedAt;
        OnPropertyChanged(nameof(PlatformLink));
        OnPropertyChanged(nameof(HasPlatformLink));
        OnPropertyChanged(nameof(PlatformLinkSummary));
        OnPropertyChanged(nameof(PlatformLinkUrl));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(HasAuthorPage));
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(AuthorPageUrl));
        OpenSourcePageCommand.NotifyCanExecuteChanged();
        OpenAuthorPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ModifiedAtDisplay));
    }
    public string Path => _mod.Path;
    public ModCategory Category => _mod.Category;
    public ModStatus Status => _mod.Status;
    public List<string> Tags => _mod.Tags;
    public long Size => _mod.Size;
    public DateTime CreatedAt => _mod.CreatedAt;
    public DateTime ImportedAt => _mod.ImportedAt;
    public string ImportedAtDisplay => FormatLocalTime(ImportedAt, "未记录（旧记录缺失导入时间）");
    public string ModifiedAtDisplay => FormatLocalTime(ModifiedAt, "未记录");

    private static string FormatLocalTime(DateTime value, string missingText)
    {
        if (value == DateTime.MinValue) return missingText;
        var timestamp = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value;
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
    public string ThumbnailPath => _mod.ThumbnailPath;

    // 显示属性
    public IReadOnlyList<ulong> ResourceTypeIds => _mod.ResourceTypeIds ?? (IReadOnlyList<ulong>)Array.Empty<ulong>();
    public string CategoryDisplay => ResourceTypeIds.Count == 0 ? "未识别资源"
        : string.Join("、", ResourceTypeIds.Select(ResourceTypeCatalog.GetDisplayName));
    public string ResourceTypeDetails => ResourceTypeIds.Count == 0 ? "未识别出游戏资源 Type ID"
        : string.Join(Environment.NewLine, ResourceTypeIds.Select(id => $"{ResourceTypeCatalog.GetDisplayName(id)} · 0x{id:X16}"));
    public string SizeFormatted => FormatSize(Size);
    public string StatusDisplay => Status.ToString();
    public bool HasTags => Tags != null && Tags.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string Hash => _mod.Hash;
    public string HashDisplay => !string.IsNullOrWhiteSpace(Hash) ? Hash
        : IsCalculatingHash ? "正在计算 SHA-256…" : HashError ?? "未计算";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HashDisplay))]
    private bool _isCalculatingHash;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HashDisplay))]
    private string? _hashError;

    public void SetHash(string hash)
    {
        _mod.Hash = hash;
        HashError = null;
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(HashDisplay));
    }
    public DateTime ModifiedAt => _mod.ModifiedAt;
    public Bitmap? IconBitmap => Icon;
    public string EnableButtonText => !IsInCurrentProfile
        ? "加入并启用当前 Profile"
        : IsEnabled ? "禁用当前 Profile Mod" : "启用当前 Profile Mod";
    public bool IsProfileEnabled => IsInCurrentProfile && IsEnabled;
    public bool IsProfileMemberDisabled => IsInCurrentProfile && !IsEnabled;
    public bool IsNotInCurrentProfile => !IsInCurrentProfile;
    public string ProfileStateText => IsProfileEnabled
        ? "已启用"
        : IsProfileMemberDisabled ? "已加入" : "未加入当前 Profile";
    public string ProfileStateDetailText => IsProfileMemberDisabled ? "未启用" : string.Empty;
    public string MetadataLine => $"{Version} · {CategoryDisplay} · {Author}";

    // 状态属性（从外部设置）
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isInCurrentProfile;

    [ObservableProperty]
    private bool _isDeployed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isFocused;

    public bool IsHighlighted => IsSelected || IsFocused;


    [ObservableProperty]
    private int _order;

    // 图标
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconBitmap))]
    private Bitmap? _icon;

    [ObservableProperty]
    private bool _isLoadingIcon;

    // Commands
    public IRelayCommand? ToggleEnabledCommand { get; set; }
    public IRelayCommand? RemoveFromProfileCommand { get; set; }
    public IRelayCommand? DeleteCommand { get; set; }
    public IRelayCommand? OpenFolderCommand { get; set; }
    public IRelayCommand? CloseDetailPanelCommand { get; set; }
    public IAsyncRelayCommand? CheckUpdatesCommand { get; set; }
    public IRelayCommand OpenSourcePageCommand { get; }
    public IRelayCommand OpenAuthorPageCommand { get; }

    public ModItemViewModel(Mod mod, IBrowserLauncher? browser = null)
    {
        _mod = mod;

        // Initialize commands
        OpenFolderCommand = new RelayCommand(OpenFolder);
        var launcher = browser ?? new SystemBrowserLauncher();
        OpenSourcePageCommand = new RelayCommand(() => OpenPage(PlatformLinkUrl, launcher), () => HasPlatformLink);
        OpenAuthorPageCommand = new RelayCommand(() => OpenPage(AuthorPageUrl, launcher), () => HasAuthorPage);
    }

    private static void OpenPage(string url, IBrowserLauncher browser)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo))
            browser.Open(uri.AbsoluteUri);
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(EnableButtonText));
        OnPropertyChanged(nameof(IsProfileEnabled));
        OnPropertyChanged(nameof(IsProfileMemberDisabled));
        OnPropertyChanged(nameof(ProfileStateText));
        OnPropertyChanged(nameof(ProfileStateDetailText));
    }

    partial void OnIsInCurrentProfileChanged(bool value)
    {
        OnPropertyChanged(nameof(EnableButtonText));
        OnPropertyChanged(nameof(IsProfileEnabled));
        OnPropertyChanged(nameof(IsProfileMemberDisabled));
        OnPropertyChanged(nameof(IsNotInCurrentProfile));
        OnPropertyChanged(nameof(ProfileStateText));
        OnPropertyChanged(nameof(ProfileStateDetailText));
    }


    /// <summary>
    /// 在文件管理器中打开模组文件夹
    /// </summary>
    private void OpenFolder()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path,
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // 打开失败，忽略
        }
    }

    /// <summary>
    /// 异步加载图标。缩略图同样由库页/配置页共享解码缓存（弱引用，不永久占内存），
    /// 避免页面刷新时重复解码同一张图。
    /// </summary>
    public async Task LoadIconAsync(CancellationToken cancellationToken = default)
    {
        if (Icon != null || IsLoadingIcon) return;
        if (string.IsNullOrEmpty(ThumbnailPath)) return;

        IsLoadingIcon = true;
        try
        {
            Icon = await ModImageCache.DecodeAsync(ThumbnailPath, 256, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 图标加载失败，保持为 null
        }
        finally
        {
            IsLoadingIcon = false;
        }
    }

    /// <summary>
    /// 从实体更新
    /// </summary>
    public void UpdateFromEntity(Mod mod)
    {
        // 基础信息由实体直接提供，无需更新
        // 只更新可变状态
    }

    /// <summary>
    /// 获取底层实体
    /// </summary>
    public Mod GetEntity() => _mod;

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
