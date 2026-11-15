using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel
{
    private readonly ModGpuUsageService? _gpuUsageService;
    private CancellationTokenSource? _gpuUsageCts;
    private Task _gpuUsageTask = Task.CompletedTask;
    private bool _gpuUsageStopped;
    private string? _gpuUsageSignature;
    private Guid? _gpuUsageProfileId;
    private bool _gpuUsageDirty = true;

    /// <summary>数据源发生变化（配置、启用状态、选项、部署方向等）时标记，允许下一次重新统计。</summary>
    public void MarkGpuUsageDirty() => _gpuUsageDirty = true;
    [ObservableProperty] private GpuUsageDisplay? _profileGpuUsage = new(null, GpuUsageScope.ProfileTotal);
    [ObservableProperty] private bool _profileGpuUsageVisible;

    public Task WaitForGpuUsageAsync() => _gpuUsageTask;

    private bool ShowGpuUsage => _settingsService?.GetSetting(SettingKeys.ShowGpuUsage, false) ?? false;

    public void RefreshGpuUsage()
    {
        if (_gpuUsageService == null || _gpuUsageStopped || !ShowGpuUsage) return;
        if (SelectedProfile is not { } profile)
        {
            // 保持占位对象，避免绑定在 null 路径上反复报错。
            ProfileGpuUsage = new(null, GpuUsageScope.ProfileTotal);
            ProfileGpuUsageVisible = false;
            return;
        }
        var cards = ProfileMods.ToArray();
        ProfileGpuUsageVisible = true;
        // 对结果有影响的内容（模组、启用状态、选项、BSL 角色、部署方向）未变时跳过重扫。
        var signature = $"{profile.Id:N}|{_bottomToTop}|{_loaderOrderLocked}|{_keepLoaderGroups}|" +
            string.Concat(cards.Where(card => !card.IsSeparator).Select(card =>
                $"{card.ModId:N}:{card.Enabled}:{(int)card.BslRole}:{card.StoredOptions.ToJson()};"));
        if (!_gpuUsageDirty && signature == _gpuUsageSignature && ProfileGpuUsage is { Usage: not null }
            && cards.All(card => card.IsSeparator || card.GpuUsage is { Usage: not null }))
            return;
        _gpuUsageDirty = false;
        _gpuUsageSignature = signature;
        _gpuUsageCts?.Cancel();
        if (_gpuUsageProfileId != profile.Id)
        {
            _gpuUsageProfileId = profile.Id;
            ProfileGpuUsage = new(null, GpuUsageScope.ProfileTotal);
        }
        var previous = _gpuUsageTask;
        var cts = new CancellationTokenSource();
        _gpuUsageCts = cts;
        foreach (var card in cards)
        {
            card.GpuUsage ??= new(null, GpuUsageScope.ProfileMod);
            card.GpuUsageVisible = true;
        }
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

    private void ClearGpuUsage()
    {
        _gpuUsageCts?.Cancel();
        _gpuUsageSignature = null;
        _gpuUsageDirty = true;
        ProfileGpuUsage = null;
        ProfileGpuUsageVisible = false;
        foreach (var card in ProfileMods)
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
