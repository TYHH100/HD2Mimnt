using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class LibraryViewModel
{
    private readonly ModGpuUsageService? _gpuUsageService;
    private CancellationTokenSource? _gpuUsageCts;
    private Task _gpuUsageTask = Task.CompletedTask;
    private bool _gpuUsageStopped;
    private string? _gpuUsageSignature;

    public Task WaitForGpuUsageAsync() => _gpuUsageTask;

    private bool ShowGpuUsage => _settingsService?.GetSetting(SettingKeys.ShowGpuUsage, false) ?? false;

    private void RefreshGpuUsage()
    {
        if (_gpuUsageService == null || _gpuUsageStopped || !ShowGpuUsage) return;
        var cards = _allMods.ToArray();
        // 模组集合未变且结果齐全时跳过，避免重复扫描并把已显示的数字重置为“计算中”。
        var signature = string.Concat(cards.Select(card => card.Id.ToString("N")));
        if (signature == _gpuUsageSignature && cards.All(card => card.GpuUsage is { Usage: not null })) return;
        _gpuUsageSignature = signature;
        _gpuUsageCts?.Cancel();
        var previous = _gpuUsageTask;
        var cts = new CancellationTokenSource();
        _gpuUsageCts = cts;
        foreach (var card in cards)
        {
            card.GpuUsage ??= new(null, GpuUsageScope.Library);
            card.GpuUsageVisible = true;
        }
        _gpuUsageTask = UpdateGpuUsageAsync(previous, cards, cts);
    }

    private async Task UpdateGpuUsageAsync(Task previous, ModItemViewModel[] cards, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            var results = await _gpuUsageService!.ScanModsAsync(cards.Select(card => card.Id), token);
            token.ThrowIfCancellationRequested();
            foreach (var card in cards)
                if (results.TryGetValue(card.Id, out var result)) card.GpuUsage = new(result, GpuUsageScope.Library);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                foreach (var card in cards) card.GpuUsage = new(null, GpuUsageScope.Library, Error: ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_gpuUsageCts, cts)) _gpuUsageCts = null;
            cts.Dispose();
        }
    }

    private void ClearGpuUsage()
    {
        _gpuUsageCts?.Cancel();
        _gpuUsageSignature = null;
        foreach (var card in _allMods)
        {
            card.GpuUsage = null;
            card.GpuUsageVisible = false;
        }
    }

    public async Task StopGpuUsageAsync()
    {
        _gpuUsageStopped = true;
        _gpuUsageCts?.Cancel();
        await _gpuUsageTask;
    }
}
