using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel
{
    private readonly ModGpuUsageService? _gpuUsageService;
    private CancellationTokenSource? _gpuUsageCts;
    private Task _gpuUsageTask = Task.CompletedTask;
    private bool _gpuUsageStopped;
    [ObservableProperty] private GpuUsageDisplay? _profileGpuUsage;

    public Task WaitForGpuUsageAsync() => _gpuUsageTask;

    public void RefreshGpuUsage()
    {
        if (_gpuUsageService == null || _gpuUsageStopped) return;
        _gpuUsageCts?.Cancel();
        ProfileGpuUsage = null;
        if (SelectedProfile is not { } profile) return;
        var previous = _gpuUsageTask;
        var cts = new CancellationTokenSource();
        _gpuUsageCts = cts;
        var cards = ProfileMods.ToArray();
        ProfileGpuUsage = new(null, GpuUsageScope.ProfileTotal);
        foreach (var card in cards) card.GpuUsage = new(null, GpuUsageScope.ProfileMod);
        _gpuUsageTask = UpdateGpuUsageAsync(previous, profile.Id, cards, cts);
    }

    private async Task UpdateGpuUsageAsync(Task previous, Guid profileId, ProfileModViewModel[] cards, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await previous;
            // 合并连续的开关、选项和排序变更。
            await Task.Delay(300, token);
            var result = await _gpuUsageService!.ScanProfileAsync(profileId, token);
            token.ThrowIfCancellationRequested();
            if (SelectedProfile?.Id != profileId) return;
            foreach (var card in cards)
                if (result.Mods.TryGetValue(card.ModId, out var usage))
                    card.GpuUsage = new(usage, GpuUsageScope.ProfileMod, result.EffectiveBytes.GetValueOrDefault(card.ModId),
                        card.Enabled, result.Total.IsComplete);
            ProfileGpuUsage = new(result.Total, GpuUsageScope.ProfileTotal);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && SelectedProfile?.Id == profileId)
            {
                ProfileGpuUsage = new(null, GpuUsageScope.ProfileTotal, Error: ex.Message);
                foreach (var card in cards) card.GpuUsage = new(null, GpuUsageScope.ProfileMod, Error: ex.Message);
            }
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
