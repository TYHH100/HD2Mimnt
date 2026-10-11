using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Utilities;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 开发工具 ViewModel
/// 用于测试和开发
/// </summary>
public partial class DevToolsViewModel : ObservableObject
{
    private readonly TestDataService _testDataService;
    private readonly ProfileService _profileService;
    private readonly NexusModsTestService _nexusModsTestService;
    private CancellationTokenSource? _operationCts;

    [ObservableProperty]
    private int _modCount = 100;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _nexusApiKey = string.Empty;

    [ObservableProperty]
    private string _nexusStatusMessage = string.Empty;

    public DevToolsViewModel(TestDataService testDataService, ProfileService profileService,
        NexusModsTestService nexusModsTestService)
    {
        _testDataService = testDataService;
        _profileService = profileService;
        _nexusModsTestService = nexusModsTestService;
        RefreshNexusStatus();
    }

    private void RefreshNexusStatus() => NexusStatusMessage = _nexusModsTestService.IsAuthenticated
        ? "本次运行已配置 N 网认证（尚未验证密钥）。"
        : "尚未配置 N 网 API 密钥。";

    [RelayCommand]
    private void ApplyNexusApiKey()
    {
        try
        {
            _nexusModsTestService.ApplyApiKey(NexusApiKey);
            NexusApiKey = string.Empty;
            RefreshNexusStatus();
        }
        catch (ArgumentException)
        {
            NexusStatusMessage = "请输入有效的 API 密钥，不能包含换行或控制字符。";
        }
    }

    [RelayCommand]
    private void ClearNexusApiKey()
    {
        _nexusModsTestService.ClearApiKey();
        NexusApiKey = string.Empty;
        RefreshNexusStatus();
    }

    partial void OnIsGeneratingChanged(bool value) => CanCancel = value;

    [RelayCommand]
    private void Cancel() => _operationCts?.Cancel();

    /// <summary>
    /// 生成测试模组
    /// </summary>
    [RelayCommand]
    private async Task GenerateTestModsAsync()
    {
        if (IsGenerating) return;

        IsGenerating = true;
        _operationCts = new CancellationTokenSource();
        StatusMessage = "正在生成测试数据...";
        Progress = 0;

        try
        {
            // 生成模组实体
            var mods = TestDataGenerator.GenerateMods(ModCount);

            var progress = new Progress<int>(completed =>
            {
                Progress = (int)((completed / (double)ModCount) * 100);
                StatusMessage = $"已生成 {completed}/{ModCount} 个模组...";
            });
            await Task.Run(
                () => _testDataService.ImportGeneratedModsAsync(mods, progress, _operationCts.Token),
                _operationCts.Token);

            StatusMessage = $"成功生成 {ModCount} 个测试模组";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "测试数据生成已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"生成失败：{ex.Message}";
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            IsGenerating = false;
        }
    }

    /// <summary>
    /// 生成启用状态测试数据
    /// </summary>
    [RelayCommand]
    private async Task GenerateEnabledStatesAsync()
    {
        if (IsGenerating) return;

        IsGenerating = true;
        _operationCts = new CancellationTokenSource();
        StatusMessage = "正在设置模组启用状态...";

        try
        {
            // 获取所有模组
            var allMods = await _testDataService.GetAllModsAsync(_operationCts.Token);
            var modList = allMods.ToList();

            if (modList.Count == 0)
            {
                StatusMessage = "没有模组可设置";
                return;
            }

            // 获取或创建默认配置文件
            var profiles = await _profileService.GetAllProfilesAsync(_operationCts.Token);
            var profile = profiles.FirstOrDefault();

            if (profile == null)
            {
                var profileId = await _profileService.CreateProfileAsync("默认配置", "测试用配置文件", _operationCts.Token);
                profile = await _profileService.GetProfileByIdAsync(profileId, _operationCts.Token);
                if (profile == null)
                {
                    StatusMessage = "无法创建配置文件";
                    return;
                }
            }

            var random = new Random();
            var enabledModIds = modList
                .Where(_ => random.Next(0, 100) < 30)
                .Select(mod => mod.Id)
                .ToArray();
            await _profileService.SetModsEnabledAsync(profile.Id, enabledModIds, true, _operationCts.Token);

            StatusMessage = $"已随机启用 {enabledModIds.Length}/{modList.Count} 个模组";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "启用状态生成已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"设置失败：{ex.Message}";
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            IsGenerating = false;
        }
    }

    /// <summary>
    /// 清除所有测试数据
    /// </summary>
    [RelayCommand]
    private async Task ClearTestDataAsync()
    {
        if (IsGenerating) return;

        IsGenerating = true;
        _operationCts = new CancellationTokenSource();
        StatusMessage = "正在清除测试数据...";

        try
        {
            var deleted = await _testDataService.ClearGeneratedModsAsync(cancellationToken: _operationCts.Token);

            StatusMessage = $"已清除 {deleted} 个测试模组";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "测试数据清理已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = $"清除失败：{ex.Message}";
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            IsGenerating = false;
        }
    }
}
