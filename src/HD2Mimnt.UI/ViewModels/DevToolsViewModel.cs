using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Utilities;
using System.Text.Json;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 开发工具 ViewModel
/// 用于测试和开发
/// </summary>
public partial class DevToolsViewModel : ObservableObject
{
    private readonly ModService _modService;
    private readonly ProfileService _profileService;

    [ObservableProperty]
    private int _modCount = 100;

    [ObservableProperty]
    private bool _isGenerating;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _progress;

    public DevToolsViewModel(ModService modService, ProfileService profileService)
    {
        _modService = modService;
        _profileService = profileService;
    }

    /// <summary>
    /// 生成测试模组
    /// </summary>
    [RelayCommand]
    private async Task GenerateTestModsAsync()
    {
        if (IsGenerating) return;

        IsGenerating = true;
        StatusMessage = "正在生成测试数据...";
        Progress = 0;

        try
        {
            // 生成模组实体
            var mods = TestDataGenerator.GenerateMods(ModCount);

            int completed = 0;
            foreach (var mod in mods)
            {
                try
                {
                    // 创建模组目录
                    Directory.CreateDirectory(mod.Path);
                    await File.WriteAllTextAsync(
                        Path.Combine(mod.Path, "manifest.json"),
                        JsonSerializer.Serialize(new
                        {
                            name = mod.Name,
                            version = mod.Version,
                            author = "HD2Mimnt"
                        }));

                    // 保存到数据库，正式导入会复制到应用存储
                    await _modService.ImportModAsync(mod.Path, default);
                }
                finally
                {
                    if (Directory.Exists(mod.Path))
                    {
                        Directory.Delete(mod.Path, true);
                    }
                }

                completed++;
                Progress = (int)((completed / (double)ModCount) * 100);
                StatusMessage = $"已生成 {completed}/{ModCount} 个模组...";
            }

            StatusMessage = $"✅ 成功生成 {ModCount} 个测试模组";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 生成失败: {ex.Message}";
        }
        finally
        {
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
        StatusMessage = "正在设置模组启用状态...";

        try
        {
            // 获取所有模组
            var allMods = await _modService.GetAllModsAsync();
            var modList = allMods.ToList();

            if (modList.Count == 0)
            {
                StatusMessage = "❌ 没有模组可设置";
                return;
            }

            // 获取或创建默认配置文件
            var profiles = await _profileService.GetAllProfilesAsync();
            var profile = profiles.FirstOrDefault();

            if (profile == null)
            {
                var profileId = await _profileService.CreateProfileAsync("默认配置", "测试用配置文件");
                profile = await _profileService.GetProfileByIdAsync(profileId);
                if (profile == null)
                {
                    StatusMessage = "❌ 无法创建配置文件";
                    return;
                }
            }

            var random = new Random();
            var enabledModIds = modList
                .Where(_ => random.Next(0, 100) < 30)
                .Select(mod => mod.Id)
                .ToArray();
            await _profileService.SetModsEnabledAsync(profile.Id, enabledModIds, true);

            StatusMessage = $"✅ 已随机启用 {enabledModIds.Length}/{modList.Count} 个模组";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 设置失败: {ex.Message}";
        }
        finally
        {
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
        StatusMessage = "正在清除测试数据...";

        try
        {
            var allMods = await _modService.GetAllModsAsync();
            var modList = allMods.ToList();

            int deleted = 0;
            foreach (var mod in modList)
            {
                if (mod.Name.StartsWith("HD2Mimnt Test ", StringComparison.Ordinal))
                {
                    await _modService.DeleteModAsync(mod.Id);

                    if (Directory.Exists(mod.Path))
                        Directory.Delete(mod.Path, true);

                    deleted++;
                }
            }

            StatusMessage = $"✅ 已清除 {deleted} 个测试模组";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ 清除失败: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }
}
