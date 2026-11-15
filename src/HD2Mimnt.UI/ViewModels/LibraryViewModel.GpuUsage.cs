using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class LibraryViewModel
{
    private readonly ModGpuUsageService? _gpuUsageService;
    private CancellationTokenSource? _gpuUsageCts;
    private Task _gpuUsageTask = Task.CompletedTask;
    private bool _gpuUsageStopped;

    public Task WaitForGpuUsageAsync() => _gpuUsageTask;

    private void RefreshGpuUsage()
    {
        if (_gpuUsageService == null || _gpuUsageStopped) return;
        _gpuUsageCts?.Cancel();
        var previous = _gpuUsageTask;
        var cts = new CancellationTokenSource();
        _gpuUsageCts = cts;
        var cards = _allMods.ToArray();
        foreach (var card in cards) card.GpuUsage = new(null, GpuUsageScope.Library);
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

    public async Task StopGpuUsageAsync()
    {
        _gpuUsageStopped = true;
        _gpuUsageCts?.Cancel();
        await _gpuUsageTask;
    }
}
