using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using System.Diagnostics;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 模组列表项 ViewModel
/// 用于模组库列表显示，包含启用状态和选择状态
/// </summary>
public partial class ModItemViewModel : ObservableObject
{
    private readonly Mod _mod;

    // 基础信息
    public Guid Id => _mod.Id;
    public string Name => _mod.Name;
    public string Version => _mod.Version;
    public string Author => _mod.Author;
    public string Description => _mod.Description;
    public string Path => _mod.Path;
    public ModCategory Category => _mod.Category;
    public ModStatus Status => _mod.Status;
    public List<string> Tags => _mod.Tags;
    public long Size => _mod.Size;
    public DateTime CreatedAt => _mod.CreatedAt;
    public DateTime ImportedAt => _mod.ImportedAt;
    public string ThumbnailPath => _mod.ThumbnailPath;

    // 显示属性
    public string CategoryDisplay => Category.ToString();
    public string SizeFormatted => FormatSize(Size);
    public string StatusDisplay => Status.ToString();
    public bool HasTags => Tags != null && Tags.Count > 0;
    public string Hash => _mod.Hash;
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
    public string DeploymentStateText => IsDeployed ? "部署状态：已部署" : "部署状态：未部署";
    public string MetadataLine => $"{Version} · {CategoryDisplay} · {Author}";

    // 状态属性（从外部设置）
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isInCurrentProfile;

    [ObservableProperty]
    private bool _isDeployed;

    [ObservableProperty]
    private bool _isSelected;


    [ObservableProperty]
    private int _order;

    // 图标
    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    private bool _isLoadingIcon;

    // Commands
    public IRelayCommand? ToggleEnabledCommand { get; set; }
    public IRelayCommand? RemoveFromProfileCommand { get; set; }
    public IRelayCommand? DeleteCommand { get; set; }
    public IRelayCommand? OpenFolderCommand { get; set; }
    public IRelayCommand? CloseDetailPanelCommand { get; set; }

    public ModItemViewModel(Mod mod)
    {
        _mod = mod;

        // Initialize commands
        OpenFolderCommand = new RelayCommand(OpenFolder);
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

    partial void OnIsDeployedChanged(bool value) => OnPropertyChanged(nameof(DeploymentStateText));


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
    /// 异步加载图标
    /// </summary>
    public async Task LoadIconAsync(CancellationToken cancellationToken = default)
    {
        if (Icon != null || IsLoadingIcon) return;
        if (string.IsNullOrEmpty(ThumbnailPath)) return;

        IsLoadingIcon = true;
        try
        {
            var bitmap = await Task.Run(() =>
            {
                if (File.Exists(ThumbnailPath))
                {
                    return new Bitmap(ThumbnailPath);
                }

                return null;
            }, cancellationToken);

            if (bitmap != null)
            {
                Icon = bitmap;
            }
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
