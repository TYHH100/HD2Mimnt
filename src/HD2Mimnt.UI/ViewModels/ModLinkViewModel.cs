using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModLinkViewModel(ModLinkService service) : ObservableObject
{
    private ModItemViewModel? _target;
    private CancellationTokenSource? _fetchCts;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _targetName = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _authorUrl = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isFetching;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private ModPlatformLink? _preview;

    public bool HasPreview => Preview != null;
    public string PreviewText => Preview is { SupportsUpdates: false } ? "已保存作者主页，可打开网页查看发布信息。" : Preview is { } link
        ? $"平台：{(link.PlatformId == "ayakamods" ? "Ayaka Mods（A 网）" : "Nexus Mods（N 网）")}\n游戏：{link.GameDomain}\n模组编号：{link.ModId}\n名称：{link.Name ?? "API 未提供"}\n最新可用版本：{link.LatestVersion ?? "API 未提供"}\n{link.Url}"
        : string.Empty;

    public void Open(ModItemViewModel mod)
    {
        CancelFetch();
        _target = mod;
        TargetName = mod.Name;
        Url = mod.PlatformLink?.Url ?? string.Empty;
        AuthorUrl = mod.AuthorPageUrl;
        Preview = mod.PlatformLink;
        Message = string.Empty;
        IsOpen = true;
    }

    partial void OnUrlChanged(string value)
    {
        CancelFetch();
        Preview = null;
        if (string.IsNullOrWhiteSpace(value)) { Message = string.Empty; return; }
        try
        {
            var link = service.Parse(value);
            Message = link.PlatformId == "ayakamods" ? $"已识别 A 网 · Helldivers 2 · 模组 {link.ModId}，点击获取信息。"
                : $"已识别 N 网 · Helldivers 2 · 模组 {link.ModId}，点击获取信息。";
        }
        catch (ArgumentException ex) { Message = ex.Message; }
    }

    private void CancelFetch()
    {
        _fetchCts?.Cancel();
        _fetchCts = null;
        IsFetching = false;
    }

    [RelayCommand]
    private async Task FetchAsync()
    {
        if (!IsOpen || IsSaving) return;
        CancelFetch();
        using var cts = new CancellationTokenSource();
        _fetchCts = cts;
        IsFetching = true;
        Preview = null;
        Message = "正在获取模组信息…";
        try
        {
            var result = await service.FetchAsync(Url, cts.Token);
            if (IsOpen && ReferenceEquals(_fetchCts, cts))
            {
                Preview = result;
                Message = "信息已获取，保存后将与当前模组绑定。";
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsOpen && ReferenceEquals(_fetchCts, cts)) Message = FormatError(ex);
        }
        finally
        {
            if (ReferenceEquals(_fetchCts, cts)) { _fetchCts = null; IsFetching = false; }
        }
    }

    private static string FormatError(Exception ex) => ModPlatformErrorFormatter.Format(ex, "获取模组信息");

    private bool CanSaveAuthorPage() => IsOpen && !IsFetching && !IsSaving;
    [RelayCommand(CanExecute = nameof(CanSaveAuthorPage))]
    private async Task SaveAuthorPageAsync()
    {
        if (_target == null) return;
        IsSaving = true;
        try
        {
            var saved = await service.SaveAuthorPageAsync(_target.Id, AuthorUrl);
            _target.ApplyPlatformLink(saved);
            IsOpen = false;
        }
        catch (Exception ex) { Message = FormatError(ex); }
        finally { IsSaving = false; }
    }

    private bool CanSave() => IsOpen && Preview?.SupportsUpdates == true && !IsFetching && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_target == null || Preview == null) return;
        IsSaving = true;
        try
        {
            var saved = await service.SaveAsync(_target.Id, Preview);
            _target.ApplyPlatformLink(saved);
            IsOpen = false;
        }
        catch (Exception ex) { Message = FormatError(ex); }
        finally { IsSaving = false; }
    }

    partial void OnIsFetchingChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAuthorPageCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsSavingChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAuthorPageCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsOpenChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        SaveAuthorPageCommand.NotifyCanExecuteChanged();
    }

    private bool CanClose() => !IsSaving;
    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        CancelFetch();
        IsOpen = false;
        Url = string.Empty;
        Preview = null;
        _target = null;
    }
}
