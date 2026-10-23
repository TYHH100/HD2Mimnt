using HD2Mimnt.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using Avalonia.Media.Imaging;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 模组选项覆盖层（应用内模态弹窗）：展示 v1 选项卡片（独立开关 + 带图片的子选项多选一）
/// 或 v0 互斥选择列表。任何改动立即经 ProfileViewModel 持久化到当前 Profile。
/// </summary>
public partial class ProfileModOptionsDialogViewModel : ObservableObject, IModOptionsSelection
{
    System.Windows.Input.ICommand IModOptionsSelection.ToggleOptionCommand => ToggleOptionCommand;
    System.Windows.Input.ICommand IModOptionsSelection.OpenGroupCommand => OpenGroupCommand;
    System.Windows.Input.ICommand IModOptionsSelection.ToggleGroupCommand => ToggleGroupCommand;
    System.Windows.Input.ICommand IModOptionsSelection.ReturnCommand => ReturnToGroupsCommand;
    private readonly ProfileViewModel _owner;

    public ProfileModViewModel TargetMod { get; }

    /// <summary>v0 模组为 true：展示互斥选择列表而不是独立开关卡片。</summary>
    public bool IsLegacyMode { get; }

    public IReadOnlyList<ProfileModLegacyChoiceViewModel> LegacyChoices { get; }

    public IReadOnlyList<ProfileModOptionCardViewModel> OptionCards { get; }

    /// <summary>一级组件分组（仅 HD2IAS 角色包导入生成）；空表示沿用单级选项列表。</summary>
    public IReadOnlyList<ProfileModOptionGroupViewModel> GroupCards { get; private set; } =
        Array.Empty<ProfileModOptionGroupViewModel>();

    private readonly Dictionary<ProfileModOptionCardViewModel, ProfileModOptionGroupViewModel> _cardGroups = new();
    private CancellationTokenSource? _imageLoad;

    /// <summary>点击卡片图片时才按需解码原图；弹窗关闭时释放，避免 4K 原图常驻。</summary>
    private CancellationTokenSource? _originalPreviewLoad;
    private Bitmap? _originalPreview;
    private string? _originalPreviewHash;
    private bool _disposed;

    [ObservableProperty]
    private ProfileModOptionGroupViewModel? _activeGroup;

    public bool HasGroups => GroupCards.Count > 0;
    public bool ShowGroupList => HasGroups && !IsGroupPage;
    public bool ShowOptionList => !IsLegacyMode && !ShowGroupList;
    public bool IsGroupPage => ActiveGroup != null;
    public IReadOnlyList<ProfileModOptionCardViewModel> VisibleOptionCards =>
        ActiveGroup?.Options ?? OptionCards;

    partial void OnActiveGroupChanged(ProfileModOptionGroupViewModel? value)
    {
        OnPropertyChanged(nameof(IsGroupPage));
        OnPropertyChanged(nameof(ShowGroupList));
        OnPropertyChanged(nameof(ShowOptionList));
        OnPropertyChanged(nameof(VisibleOptionCards));
        OnPropertyChanged(nameof(Title));
    }

    public string Title => IsGroupPage ? $"{TargetMod.ModName} · {ActiveGroup!.Name}" : TargetMod.ModName;

    public ProfileModOptionsDialogViewModel(ProfileViewModel owner, ProfileModViewModel targetMod)
    {
        _owner = owner;
        TargetMod = targetMod;

        var manifest = targetMod.ManifestRef;
        var state = targetMod.StoredOptions;
        var modPath = targetMod.ModPath;

        if (manifest?.ManifestVersion == ManifestVersion.V1 && manifest.Options is { Count: > 0 })
        {
            IsLegacyMode = false;
            var cards = new List<ProfileModOptionCardViewModel>(manifest.Options.Count);
            for (var i = 0; i < manifest.Options.Count; i++)
            {
                var option = manifest.Options[i];

                var subChoices = new List<ProfileModSubOptionChoiceViewModel>();
                if (option.SubOptions is { Count: > 0 })
                {
                    var selectedIndex = i < state.OptionSelections?.Count
                        ? Math.Clamp(state.OptionSelections[i], 0, option.SubOptions.Count - 1)
                        : 0;
                    for (var s = 0; s < option.SubOptions.Count; s++)
                    {
                        var sub = option.SubOptions[s];
                        subChoices.Add(new ProfileModSubOptionChoiceViewModel
                        {
                            Name = sub.Name,
                            Description = sub.Description,
                            ImagePath = ResolveImagePath(modPath, sub.Image),
                            IsSelected = s == selectedIndex
                        });
                    }
                }

                cards.Add(new ProfileModOptionCardViewModel
                {
                    Name = option.Name,
                    Description = option.Description,
                    IsEnabled = i < state.OptionToggles?.Count
                        ? state.OptionToggles[i]
                        : option.DefaultEnabled ?? true,
                    ImagePath = ResolveImagePath(modPath, option.Image),
                    GroupId = option.GroupId,
                    SubOptions = subChoices
                });
            }

            OptionCards = cards;
            GroupCards = BuildOptionGroups(manifest, cards, modPath);
            LegacyChoices = Array.Empty<ProfileModLegacyChoiceViewModel>();
        }
        else if (manifest?.LegacyOptions is { Count: > 0 })
        {
            IsLegacyMode = true;
            var selectedIndex = Math.Clamp(state.LegacySelectedIndex ?? 0, 0, manifest.LegacyOptions.Count - 1);
            LegacyChoices = manifest.LegacyOptions
                .Select((name, index) => new ProfileModLegacyChoiceViewModel
                {
                    Name = name,
                    IsSelected = index == selectedIndex
                })
                .ToList();
            OptionCards = Array.Empty<ProfileModOptionCardViewModel>();
        }
        else
        {
            IsLegacyMode = false;
            OptionCards = Array.Empty<ProfileModOptionCardViewModel>();
            LegacyChoices = Array.Empty<ProfileModLegacyChoiceViewModel>();
        }
    }

    [RelayCommand]
    public void ToggleOption(ProfileModOptionCardViewModel? card)
    {
        if (card == null) return;
        card.IsEnabled = !card.IsEnabled;
        SyncGroup(card);
        SaveState();
    }

    /// <summary>一级组件开关：同步组件内所有选项，并保存到当前配置。</summary>
    [RelayCommand]
    public void ToggleGroup(ProfileModOptionGroupViewModel? group)
    {
        if (group == null) return;
        group.IsEnabled = !group.IsEnabled;
        foreach (var card in group.Options) card.IsEnabled = group.IsEnabled;
        SaveState();
    }

    /// <summary>进入二级组件选项页继续配置。</summary>
    [RelayCommand]
    public void OpenGroup(ProfileModOptionGroupViewModel? group)
    {
        if (group is not { CanOpen: true }) return;
        ActiveGroup = group;
    }

    /// <summary>保存二级页改动并回到一级组件列表。</summary>
    [RelayCommand]
    public void ReturnToGroups()
    {
        SaveState();
        ActiveGroup = null;
    }

    private void SyncGroup(ProfileModOptionCardViewModel card)
    {
        // 一级组件开关表示“该组件仍参与部署”，只要组件内还有选项启用就保持勾选；
        // 关闭某个子选项只排除该选项的文件，不能把整个组件显示成未启用。
        if (_cardGroups.TryGetValue(card, out var group))
            group.IsEnabled = group.Options.Any(option => option.IsEnabled);
    }

    private ProfileModOptionGroupViewModel[] BuildOptionGroups(ModManifest manifest,
        IReadOnlyList<ProfileModOptionCardViewModel> cards, string modPath)
    {
        // 只有 HD2IAS 导入生成的分组才切换成两级配置页；普通模组保持单级列表。
        if (manifest.OptionGroups is not { Count: > 0 } declaredGroups)
            return Array.Empty<ProfileModOptionGroupViewModel>();
        var groups = new List<ProfileModOptionGroupViewModel>();
        foreach (var declared in declaredGroups)
        {
            var options = cards.Where(card => card.GroupId != null
                && card.GroupId.Equals(declared.Id, StringComparison.Ordinal)).ToList();
            if (options.Count == 0) continue;
            groups.Add(CreateGroup(declared.Id, declared.Name, declared.Description, declared.Image, options, modPath));
        }
        // 未分组的选项补一个合成分组，避免存在分组时被一级列表隐藏。
        foreach (var card in cards.Where(card => !_cardGroups.ContainsKey(card)))
            groups.Add(CreateGroup($"option-{groups.Count}", card.Name, card.Description, null, [card], modPath));
        return groups.ToArray();
    }

    private ProfileModOptionGroupViewModel CreateGroup(string id, string name, string description, string? image,
        IReadOnlyList<ProfileModOptionCardViewModel> options, string modPath)
    {
        var group = new ProfileModOptionGroupViewModel
        {
            Id = id, Name = name, Description = description,
            // 初始勾选：组件内存在启用的选项即视为组件启用，与子选项逐项开关语义一致。
            IsEnabled = options.Any(option => option.IsEnabled),
            ImagePath = ResolveImagePath(modPath, image),
            Options = options
        };
        foreach (var option in options) _cardGroups[option] = group;
        return group;
    }

    /// <summary>子选项多选一：同卡片内互斥，选中项高亮。</summary>
    public void SelectSubOption(ProfileModSubOptionChoiceViewModel? choice)
    {
        if (choice == null) return;
        foreach (var card in OptionCards)
        {
            if (card.SubOptions.Contains(choice))
            {
                foreach (var sub in card.SubOptions)
                {
                    sub.IsSelected = ReferenceEquals(sub, choice);
                }
                break;
            }
        }
        SaveState();
    }

    /// <summary>v0 互斥下拉：列表内单选。</summary>
    public void SelectLegacyChoice(ProfileModLegacyChoiceViewModel? choice)
    {
        if (choice == null) return;
        foreach (var item in LegacyChoices)
        {
            item.IsSelected = ReferenceEquals(item, choice);
        }
        SaveState();
    }

    public void Close() => _owner.CloseModOptions();

    /// <summary>
    /// 卡片图片点击：仅在需要时按原图尺寸解码，关闭预览或弹窗时释放；
    /// 没有原图路径时回退到已解码的小图。这样打开带 4K 预览图的 HD2IAS 选项不会一次性占用数百 MiB。
    /// </summary>
    public void ShowImagePreview(Bitmap? image, string? title)
    {
        if (image == null) return;
        if (FindImageHolder(image) is { } holder && !string.IsNullOrEmpty(holder.ImagePath))
        {
            _ = ShowOriginalPreviewAsync(holder, title);
            return;
        }

        _owner.ShowImagePreview(image, title);
    }

    private async Task ShowOriginalPreviewAsync(ProfileModImageHolderViewModel holder, string? title)
    {
        CancelOriginalPreviewLoad();
        var cancellation = new CancellationTokenSource();
        _originalPreviewLoad = cancellation;
        try
        {
            var hash = holder.ImagePath is { Length: > 0 } path
                ? await ModImageCache.GetContentHashAsync(path, cancellation.Token)
                : null;

            // 内容相同的另一份副本（例如 HD2IAS 组件目录里的同一张预览图）直接复用已解码原图。
            if (hash != null && _originalPreview is { } shared
                && string.Equals(hash, _originalPreviewHash, StringComparison.Ordinal))
            {
                if (!_disposed && !cancellation.IsCancellationRequested) _owner.ShowImagePreview(shared, title);
                return;
            }

            var original = await holder.LoadOriginalImageAsync(cancellation.Token);
            if (original == null)
            {
                if (!_disposed) _owner.ShowImagePreview(holder.Image, title);
                return;
            }

            // 解码期间弹窗可能已关闭；此时立即释放，不把原图挂到已销毁的弹窗上。
            if (_disposed || holder.IsDisposed || cancellation.IsCancellationRequested)
            {
                original.Dispose();
                return;
            }

            ReleaseOriginalPreview();
            _originalPreview = original;
            _originalPreviewHash = hash;
            _owner.ShowImagePreview(original, title);
        }
        catch (OperationCanceledException)
        {
            // 弹窗关闭或切换到另一张图片时放弃本次加载。
        }
    }

    private ProfileModImageHolderViewModel? FindImageHolder(Bitmap image)
        => GroupCards.Cast<ProfileModImageHolderViewModel>()
            .Concat(OptionCards)
            .Concat(OptionCards.SelectMany(card => card.SubOptions))
            .FirstOrDefault(holder => ReferenceEquals(holder.Image, image));

    /// <summary>释放覆盖层中的原图预览；覆盖层正显示它时先关闭覆盖层。</summary>
    private void ReleaseOriginalPreview()
    {
        var original = _originalPreview;
        _originalPreview = null;
        _originalPreviewHash = null;
        if (original == null) return;
        if (_owner.IsImagePreviewShowing(original)) _owner.CloseImagePreview();
        original.Dispose();
    }

    private void CancelOriginalPreviewLoad()
    {
        var cancellation = _originalPreviewLoad;
        _originalPreviewLoad = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    /// <summary>把覆盖层当前的选择收集为可持久化状态。</summary>
    internal ProfileModOptionsState BuildOptionsState()
    {
        var state = new ProfileModOptionsState();
        if (IsLegacyMode)
        {
            for (var i = 0; i < LegacyChoices.Count; i++)
            {
                if (LegacyChoices[i].IsSelected)
                {
                    state.LegacySelectedIndex = i;
                    break;
                }
            }

            if (state.LegacySelectedIndex is null && LegacyChoices.Count > 0)
            {
                state.LegacySelectedIndex = 0;
            }
        }
        else
        {
            state.OptionToggles = OptionCards.Select(card => card.IsEnabled).ToList();
            state.OptionSelections = OptionCards.Select(card =>
            {
                for (var s = 0; s < card.SubOptions.Count; s++)
                {
                    if (card.SubOptions[s].IsSelected)
                    {
                        return s;
                    }
                }

                return 0;
            }).ToList();
        }

        return state;
    }

    private void SaveState()
    {
        TargetMod.StoredOptions = BuildOptionsState();
        _ = _owner.PersistModOptionsAsync(TargetMod);
    }

    /// <summary>后台加载组件/选项/子选项预览图；关闭弹窗时取消并释放已解码位图。</summary>
    public async Task LoadImagesAsync()
    {
        _imageLoad?.Cancel();
        _imageLoad?.Dispose();
        var cancellation = new CancellationTokenSource();
        _imageLoad = cancellation;
        var token = cancellation.Token;
        try
        {
            foreach (var group in GroupCards)
            {
                await group.LoadImageAsync(token);
            }
            foreach (var card in OptionCards)
            {
                await card.LoadImageAsync(token);
                foreach (var sub in card.SubOptions)
                {
                    await sub.LoadImageAsync(token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>关闭弹窗后释放本弹窗解码的预览图（含按需解码的原图），避免反复打开累积原生位图内存。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelOriginalPreviewLoad();
        ReleaseOriginalPreview();

        // 内容相同的卡片共享同一份位图：按实例去重后统一释放，不能逐个卡片重复 Dispose。
        var images = new HashSet<Bitmap>(ReferenceEqualityComparer.Instance);
        foreach (var card in OptionCards)
        {
            if (card.Image is { } image) images.Add(image);
            foreach (var sub in card.SubOptions)
                if (sub.Image is { } subImage) images.Add(subImage);
        }
        foreach (var group in GroupCards)
            if (group.Image is { } groupImage) images.Add(groupImage);

        var cancellation = _imageLoad;
        _imageLoad = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        foreach (var card in OptionCards) card.Dispose();
        foreach (var group in GroupCards) group.Dispose();
        foreach (var image in images)
        {
            ModImageCache.Forget(image);
            image.Dispose();
        }
    }

    /// <summary>预览覆盖层当前展示的是否是本弹窗持有的位图。</summary>
    internal bool OwnsImage(Bitmap? image)
        => image != null && (OptionCards.Any(card => ReferenceEquals(card.Image, image)
            || card.SubOptions.Any(sub => ReferenceEquals(sub.Image, image)))
            || GroupCards.Any(group => ReferenceEquals(group.Image, image)));

    /// <summary>把清单里的相对图片路径解析为模组目录内的绝对路径（越出模组目录或文件缺失返回 null）。</summary>
    internal static string? ResolveImagePath(string modPath, string? relativeImage)
    {
        if (string.IsNullOrWhiteSpace(relativeImage) || string.IsNullOrEmpty(modPath))
        {
            return null;
        }

        try
        {
            var fullRoot = Path.GetFullPath(modPath);
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativeImage));
            if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 选项/组件卡片共用的预览图持有者：后台解码、关闭弹窗时取消并释放位图，
/// 防止反复打开选项弹窗后原生位图内存持续累积。
/// </summary>
