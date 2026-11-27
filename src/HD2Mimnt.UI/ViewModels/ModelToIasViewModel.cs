using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModelToIasViewModel : ObservableObject
{
    private readonly IModelToIasConversionService _service;
    private readonly NotificationService _notifications;
    private CancellationTokenSource? _cancellation;

    public ModelToIasViewModel(IModelToIasConversionService service, NotificationService notifications)
    {
        _service = service;
        _notifications = notifications;
    }

    public ObservableCollection<ModelToIasCandidateItem> Candidates { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    public event EventHandler? ConversionCompleted;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var candidates = await _service.GetCandidatesAsync();
            Candidates.Clear();
            foreach (var candidate in candidates) Candidates.Add(new ModelToIasCandidateItem(candidate));
            Status = Candidates.Count == 0
                ? "模组库中没有找到可转换的普通护甲/头盔模型。"
                : $"找到 {Candidates.Count} 个可转换的普通模型。";
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
    private async Task ConvertAsync(ModelToIasCandidateItem? item)
    {
        if (item is null || IsBusy) return;
        IsBusy = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(message => Status = message);
            var result = await _service.ConvertAsync(item.ModId, progress: progress, token: _cancellation.Token);
            Status = result.SourceDisabled
                ? $"已生成「{result.Name}」并加入当前配置；源普通模型已禁用。请重新部署并重启游戏。"
                : $"已生成「{result.Name}」并导入模组库；当前没有配置文件，源模组未改动。";
            _notifications.ShowSuccess(Status, "IAS");
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
            _notifications.ShowError(Status, "IAS");
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Status = "正在取消转换…";
        _cancellation?.Cancel();
    }
}

public sealed class ModelToIasCandidateItem(ModelToIasCandidate candidate)
{
    public Guid ModId { get; } = candidate.ModId;
    public string Name { get; } = candidate.Name;
    public string Path { get; } = candidate.Path;
    public string UnitSummary => candidate.UnitCount + " 个可识别 Unit";
}
