using HD2Mimnt.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using Avalonia.Media.Imaging;
using Avalonia.Media;

namespace HD2Mimnt.UI.ViewModels;

public sealed record ConflictModTarget(Guid ModId, string ModName);

public partial class ProfileModViewModel : ObservableObject
{
    public ModItemViewModel? SourceInfo { get; init; }
    [ObservableProperty]
    private bool _isConflictLocation;

    public IEnumerable<ConflictModTarget> ConflictTargets { get; private set; } = [];

    [ObservableProperty]
    private bool _hasConflict;

    public string ConflictTooltip
    {
        get
        {
            if (!HasConflict) return string.Empty;
            var preview = ConflictTargets.Take(9).ToArray();
            return "与以下模组存在覆盖冲突：\n"
                + string.Join("\n", preview.Take(8).Select(target => target.ModName))
                + (preview.Length > 8 ? "\n…点击感叹号查看全部关联模组。" : "")
                + "\n\n详细信息请查看“冲突”页。";
        }
    }

    internal void ApplyConflictStatus(bool hasConflict, ConflictCardIndex index)
    {
        HasConflict = hasConflict;
        ConflictTargets = hasConflict ? index.GetTargets(ModId) : [];
        OnPropertyChanged(nameof(ConflictTooltip));
    }

    public bool IsSeparator { get; init; }
    public bool IsUserSeparator { get; init; }
    public int? MemberCount { get; set; }
    [ObservableProperty]
    private bool _isCollapsed;

    [ObservableProperty]
    private int _groupModCount;

    /// <summary>展开时的箭头方向：常规部署向下，从下到上部署向上。</summary>
    [ObservableProperty]
    private double _chevronAngle = 90;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomSeparatorColor))]
    [NotifyPropertyChangedFor(nameof(SeparatorColorBrush))]
    private string _separatorColor = string.Empty;
    public bool HasCustomSeparatorColor => Color.TryParse(SeparatorColor, out _);
    public IBrush? SeparatorColorBrush => Color.TryParse(SeparatorColor, out var color) ? new SolidColorBrush(color) : null;
    public bool IsExpanded => !IsCollapsed;

    /// <summary>是否位于展开分组的连贯半透明色板上（成员卡片）。</summary>
    [ObservableProperty]
    private bool _isInsideGroup;

    /// <summary>分组色板的自定义颜色（已按固定透明度混合）；null 表示跟随主题，由样式回退到主题强调色。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomGroupTint))]
    private IBrush? _groupTintBrush;
    public bool HasCustomGroupTint => GroupTintBrush != null;

    /// <summary>色板的首/尾成员，决定连贯底色的圆角方向。</summary>
    [ObservableProperty]
    private bool _isSlabStart;

    [ObservableProperty]
    private bool _isSlabEnd;
    public BslModRole BslRole { get; init; }
    public bool IsBslMember => BslRole != BslModRole.None;
    public bool LoaderLockEnabled { get; init; } = true;
    public bool IsFixedGroupMember => LoaderLockEnabled && IsBslMember;
    public bool IsBslLoader => BslProfilePolicy.IsLoader(BslRole);
    public bool IsSmoothBoot => BslRole == BslModRole.SmoothBoot;
    public bool IsLuaDependency => BslRole == BslModRole.Dependency;
    public bool HasLuaResources { get; init; }
    public bool IsLuaMod => HasLuaResources || IsBslMember;
    public string PriorityLabel => IsSmoothBoot ? "SmoothBoot · 最高优先级"
        : BslRole == BslModRole.BmlLoader ? "Boot Mod Loader · 固定在依赖之后"
        : IsBslLoader ? "BSL / MDL · 固定在依赖之后" : "依赖加载器";
    public bool CanSelect => !IsSeparator && (!LoaderLockEnabled || !IsBslLoader && !IsSmoothBoot);
    public bool CanDrag => CanSelect || IsUserSeparator;

    [ObservableProperty]
    private Guid _profileId;

    [ObservableProperty]
    private Guid _modId;

    [ObservableProperty]
    private string _modName = string.Empty;

    [ObservableProperty]
    private string _modVersion = string.Empty;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _isDeployed;

    [ObservableProperty]
    private int _order;

    /// <summary>列表中的展示位次（1 起），与部署顺序一致。</summary>
    [ObservableProperty]
    private int _position;

    /// <summary>多选拖动：Ctrl+点击标记，随拖动整体移动。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isFocused;

    public bool IsHighlighted => IsSelected || IsFocused;

    /// <summary>模组库目录，供“打开模组目录”使用。</summary>
    [ObservableProperty]
    private string _modPath = string.Empty;

    [ObservableProperty]
    private string _thumbnailPath = string.Empty;

    [ObservableProperty]
    private Bitmap? _icon;

    partial void OnIconChanged(Bitmap? value)
    {
        if (SourceInfo != null) SourceInfo.Icon = value;
    }

    private bool _isLoadingIcon;

    public string PositionText => Position.ToString("00");

    partial void OnPositionChanged(int value) => OnPropertyChanged(nameof(PositionText));

    partial void OnIsCollapsedChanged(bool value) => OnPropertyChanged(nameof(IsExpanded));

    // ── 模组选项（卡片只保留入口；选项/子选项在覆盖层里编辑） ──

    /// <summary>模组是否带选项定义（v0 互斥下拉 / v1 独立开关+子选项）。</summary>
    [ObservableProperty]
    private bool _hasOptions;

    /// <summary>模组清单（含选项定义），供选项覆盖层构建。</summary>
    internal ModManifest? ManifestRef { get; set; }

    /// <summary>已保存/正在编辑的选项选择状态。</summary>
    internal ProfileModOptionsState StoredOptions { get; set; } = new();

    internal void InitializeOptions(ModManifest? manifest, ProfileModOptionsState state)
    {
        ManifestRef = manifest;
        StoredOptions = state;
        HasOptions = ProfileModOptionResolver.HasOptions(manifest);
    }

    /// <summary>与库页共享缩略图解码缓存；失败保持 null 以显示序号占位。</summary>
    public async Task LoadIconAsync(CancellationToken cancellationToken = default)
    {
        if (Icon != null || _isLoadingIcon) return;
        if (string.IsNullOrEmpty(ThumbnailPath)) return;

        _isLoadingIcon = true;
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
            _isLoadingIcon = false;
        }
    }
}
