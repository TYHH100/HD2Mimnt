using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>工具页「HD2CL 角色库」：列出可转换的 HD2IAS 模组并执行一键转换。</summary>
public partial class Hd2ClViewModel : ObservableObject
{
    private readonly IHd2ClConversionService _service;
    private readonly NotificationService _notifications;
    private readonly IHd2ClVariantPrompt? _variantPrompt;

    public Hd2ClViewModel(IHd2ClConversionService service, NotificationService notifications,
        IHd2ClVariantPrompt? variantPrompt = null)
    {
        _service = service;
        _notifications = notifications;
        _variantPrompt = variantPrompt;
    }

    public ObservableCollection<Hd2ClCandidateItem> Candidates { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    public event EventHandler? ConversionCompleted;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var candidates = await _service.GetCandidatesAsync();
            Candidates.Clear();
            foreach (var candidate in candidates)
                Candidates.Add(new Hd2ClCandidateItem(candidate));
            Status = Candidates.Count == 0
                ? "模组库中没有包含 IAS 配套的模组。"
                : $"找到 {Candidates.Count} 个可转换的 HD2IAS 模组。";
        }
        catch (Exception ex)
        {
            Status = "读取失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ConvertAsync(Hd2ClCandidateItem? item)
    {
        if (item is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var (variant, canceled) = await Hd2ClVariantSelection.ResolveAsync(_service, _variantPrompt, item.ModId);
            if (canceled)
            {
                Status = "已取消转换。";
                return;
            }
            var progress = new Progress<string>(message => Status = message);
            var result = await _service.ConvertAsync(item.ModId, variant?.ComponentFolders, variant?.Name, progress);
            Status = result.SourceDisabled
                ? $"已生成「{result.Name}」并加入当前配置；源 IAS 模组已在当前配置中禁用。"
                : $"已生成「{result.Name}」并导入模组库；当前没有配置文件，源 IAS 模组未改动。";
            _notifications.ShowSuccess(Status, "HD2CL");
            ConversionCompleted?.Invoke(this, EventArgs.Empty);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "转换已取消。";
        }
        catch (Exception ex)
        {
            Status = "转换失败：" + ex.Message;
            _notifications.ShowError(Status, "HD2CL");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed class Hd2ClCandidateItem(Hd2ClCandidate candidate)
{
    public Guid ModId { get; } = candidate.ModId;
    public string Name { get; } = candidate.Name;
    public string Path { get; } = candidate.Path;
    public string CompanionSummary => candidate.CompanionComponents + " 个配套组件";
}
