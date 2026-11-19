using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 工具页「HD2CL 角色库 → MMD 资源包」：选择原始动作资源、检查曲目/媒体，再转换成独立播放适配包。
/// 检查与转换都交给应用服务，转换完成后导入模组库并加入当前配置。
/// </summary>
public partial class MmdAdaptationViewModel : ObservableObject
{
    private const string EmptyNotes = "检查后显示曲目、时长、音乐、镜头和原包选项。";
    private const string EmptyHint = "选择原始动作资源后，点击“检查内容”。";

    private readonly IMmdAdaptationService _service;
    private readonly NotificationService _notifications;
    private readonly SettingsService _settings;
    private readonly IHd2ClMmdSourceInfoPrompt? _prompt;
    private CancellationTokenSource? _cancellation;
    private string? _idleOverride;
    private bool _syncing;
    private bool _ready;

    public MmdAdaptationViewModel(IMmdAdaptationService service, NotificationService notifications,
        SettingsService settings, IHd2ClMmdSourceInfoPrompt? prompt = null)
    {
        _service = service;
        _notifications = notifications;
        _settings = settings;
        _prompt = prompt;
    }

    public ObservableCollection<MmdOptionItem> Options { get; } = new();

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _notes = EmptyNotes;

    [ObservableProperty]
    private string _noteHint = EmptyHint;

    [ObservableProperty]
    private MmdOptionItem? _selectedOption;

    [ObservableProperty]
    private int _idleModeIndex;

    public bool IsIdleLoop
    {
        get => IdleModeIndex == 0;
        set { if (value && IdleModeIndex != 0) IdleModeIndex = 0; }
    }

    public bool IsIdleOnce
    {
        get => IdleModeIndex == 1;
        set { if (value && IdleModeIndex != 1) IdleModeIndex = 1; }
    }

    [ObservableProperty]
    private MmdAdaptationSourceInfo? _sourceInfo;

    [ObservableProperty]
    private bool _canConvert;

    public event EventHandler? ConversionCompleted;

    /// <summary>上次导出压缩包使用的目录；没有记录时由文件夹选择器使用系统默认位置。</summary>
    public string SuggestedExportDirectory
        => _settings.GetSetting(SettingKeys.Hd2ClExportDirectory, string.Empty);

    partial void OnSourcePathChanged(string value) => Invalidate(sourceChanged: true);

    partial void OnSelectedOptionChanged(MmdOptionItem? value) => Invalidate(sourceChanged: false);

    partial void OnIdleModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsIdleLoop));
        OnPropertyChanged(nameof(IsIdleOnce));
        if (_syncing || IsBusy) return;
        _idleOverride = value == 1 ? "once" : "loop";
        Invalidate(sourceChanged: false);
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy) return;
        var source = SourcePath.Trim().Trim('"');
        if (source.Length == 0)
        {
            Status = "请先选择 MMD 原始资源包或目录。";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        CanConvert = false;
        try
        {
            Status = "正在检查所选 MMD 资源…";
            var progress = new Progress<string>(message => Status = message);
            var inspection = await _service.InspectAsync(source, SelectedOption?.Key, SourceInfo,
                _idleOverride, progress, cancellation.Token);
            ApplyInspection(inspection);
        }
        catch (OperationCanceledException)
        {
            Status = "检查已取消。";
        }
        catch (Exception error)
        {
            Status = "检查失败：" + error.Message;
            _notifications.ShowError(Status, "HD2CL MMD");
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (IsBusy || !CanConvert) return;
        var source = SourcePath.Trim().Trim('"');
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        CanConvert = false;
        try
        {
            Status = "正在转换 MMD 资源包…";
            var progress = new Progress<string>(message => Status = message);
            var result = await _service.ConvertAsync(source, SelectedOption?.Key, SourceInfo,
                _idleOverride, progress, cancellation.Token);
            Status = result.ProfileId is null
                ? $"已生成「{result.Name}」并导入模组库；当前没有配置文件，未加入任何配置。"
                : $"已生成「{result.Name}」并加入当前配置启用。";
            _notifications.ShowSuccess(Status, "HD2CL MMD");
            ConversionCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "转换已取消。";
        }
        catch (Exception error)
        {
            Status = "转换失败：" + error.Message;
            _notifications.ShowError(Status, "HD2CL MMD");
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            CanConvert = _ready;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>只导出压缩包（7z）到指定目录：不导入模组库、不改当前配置。</summary>
    public async Task ExportAsync(string directory)
    {
        if (IsBusy || !CanConvert) return;
        if (string.IsNullOrWhiteSpace(directory))
        {
            Status = "请选择导出目录。";
            return;
        }
        var source = SourcePath.Trim().Trim('"');
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        CanConvert = false;
        try
        {
            Status = "正在导出 MMD 资源包…";
            var progress = new Progress<string>(message => Status = message);
            var result = await _service.ExportAsync(source, directory, SelectedOption?.Key, SourceInfo,
                _idleOverride, progress, cancellation.Token);
            await RememberExportDirectoryAsync(directory);
            Status = $"已导出「{result.Name}」：{result.ArchivePath}";
            _notifications.ShowSuccess(Status, "HD2CL MMD");
        }
        catch (OperationCanceledException)
        {
            Status = "导出已取消。";
        }
        catch (Exception error)
        {
            Status = "导出失败：" + error.Message;
            _notifications.ShowError(Status, "HD2CL MMD");
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            CanConvert = _ready;
        }
    }

    private async Task RememberExportDirectoryAsync(string directory)
    {
        try { await _settings.SetSettingAsync(SettingKeys.Hd2ClExportDirectory, directory); }
        catch { }
    }

    [RelayCommand]
    private async Task EditSourceInfoAsync()
    {
        if (IsBusy || _prompt is null) return;
        var updated = await _prompt.EditAsync(SourceInfo);
        if (updated is null) return;
        SourceInfo = updated;
        Invalidate(sourceChanged: false);
    }

    internal void ApplyInspection(MmdAdaptationInspection inspection)
    {
        var selectedKey = inspection.SelectedOption;
        _syncing = true;
        try
        {
            Options.Clear();
            var names = inspection.SourceOptions.Select(option => option.Name).ToList();
            for (var index = 0; index < inspection.SourceOptions.Count; index++)
            {
                var option = inspection.SourceOptions[index];
                var display = names.Count(name => name == option.Name) == 1
                    ? option.Name
                    : option.Name + "（" + (index + 1) + "）";
                Options.Add(new MmdOptionItem(option.Key, display));
            }
            SelectedOption = Options.FirstOrDefault(option => option.Key == selectedKey);
            IdleModeIndex = inspection.IdlePlayback == "once" ? 1 : 0;
        }
        finally
        {
            _syncing = false;
        }

        Notes = FormatNotes(inspection);
        var ready = inspection.Status == MmdAdaptationStatus.Ready;
        NoteHint = ready ? "检查完成，请核对曲目与媒体信息。" : "请选择曲包或原包动作选项，然后重新检查。";
        Status = ready ? $"检查完成：{inspection.Name}" : "来源包含多个曲包或加载选项，请选择要转换的一项后重新检查。";
        _ready = ready;
        CanConvert = ready;
    }

    private static string FormatNotes(MmdAdaptationInspection inspection)
    {
        var lines = new List<string>
        {
            inspection.Name,
            string.Empty,
            "待机播放方式：" + IdleLabel(inspection.IdlePlayback)
        };
        foreach (var track in inspection.Tracks)
        {
            if (track.Title != inspection.Name) lines.Add("曲目：" + track.Title);
            var seconds = (int)track.Duration;
            lines.Add($"时长：{seconds / 60}:{seconds % 60:00}  ·  {track.Fps} FPS");
            lines.Add("音乐：" + (track.Music ? "已包含" : "无") + "  ·  镜头：" + (track.Camera ? "已包含" : "无"));
        }
        if (inspection.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(inspection.Warnings);
        }
        return string.Join('\n', lines);
    }

    private static string IdleLabel(string value) => value == "once" ? "完整演出（播放一次）" : "持续姿势（循环播放）";

    private void Invalidate(bool sourceChanged)
    {
        if (_syncing || IsBusy) return;
        if (sourceChanged)
        {
            _syncing = true;
            try
            {
                Options.Clear();
                SelectedOption = null;
                IdleModeIndex = 0;
            }
            finally
            {
                _syncing = false;
            }
            _idleOverride = null;
            Notes = EmptyNotes;
            NoteHint = EmptyHint;
        }
        _ready = false;
        CanConvert = false;
        Status = "来源已更改，请重新检查。";
    }
}

public sealed class MmdOptionItem(string key, string display)
{
    public string Key { get; } = key;
    public string Display { get; } = display;
}
