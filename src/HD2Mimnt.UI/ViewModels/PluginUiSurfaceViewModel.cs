using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 声明式界面的运行时宿主：持有一组区块，负责把用户操作转成插件命令调用，
/// 并把插件返回的增量更新应用到区块集合。
///
/// 插件只回传数据（区块 / 文本），不会拿到任何 UI 对象、视图模型或密钥。
/// </summary>
public sealed partial class PluginUiSurfaceViewModel : ObservableObject
{
    private readonly IPluginService? _pluginService;
    private readonly PluginUiBundle? _bundle;

    public PluginUiSurfaceViewModel(
        PluginUiBundle bundle,
        string surfaceId,
        string title,
        string? description,
        string? icon,
        IEnumerable<PluginUiBlock> initialBlocks,
        bool isPage,
        IPluginService? pluginService)
    {
        _bundle = bundle;
        _pluginService = pluginService;
        PluginId = bundle.PluginId;
        PluginName = bundle.PluginName;
        SurfaceId = surfaceId;
        Title = string.IsNullOrWhiteSpace(title) ? bundle.PluginName : title;
        Description = description;
        Icon = icon;
        IsPage = isPage;

        foreach (var block in initialBlocks)
        {
            Blocks.Add(block);
        }
    }

    public string PluginId { get; }

    public string PluginName { get; }

    /// <summary>页面或浮窗的标识，随界面事件回传，插件据此区分来源。</summary>
    public string SurfaceId { get; }

    public string Title { get; }

    public string? Description { get; }

    public string? Icon { get; }

    public bool IsPage { get; }

    public ObservableCollection<PluginUiBlock> Blocks { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>请求打开同插件的另一个浮窗。</summary>
    public event EventHandler<PluginDialogRequestEventArgs>? DialogRequested;

    /// <summary>命令返回了 Markdown 文本结果，由视图弹出结果窗口。</summary>
    public event EventHandler<PluginCommandResultEventArgs>? ResultReady;

    /// <summary>权限是否已授予；未声明权限视为不需要授权。</summary>
    public bool HasPermission(string? permission) => _bundle?.HasPermission(permission) ?? true;

    /// <summary>按 id 找到同插件的浮窗声明，并生成可渲染的独立界面。</summary>
    public PluginUiSurfaceViewModel? CreateDialogSurface(string dialogId)
        => CreateDialogSurface(_bundle, dialogId, _pluginService);

    /// <summary>从 bundle 直接创建浮窗界面（供功能菜单等没有现成页面的场景使用）。</summary>
    public static PluginUiSurfaceViewModel? CreateDialogSurface(
        PluginUiBundle? bundle,
        string dialogId,
        IPluginService? pluginService)
    {
        var dialog = bundle?.Ui.Dialogs.FirstOrDefault(
            d => string.Equals(d.Id, dialogId, StringComparison.OrdinalIgnoreCase));
        if (dialog is null || bundle is null) return null;

        return new PluginUiSurfaceViewModel(
            bundle,
            dialog.Id,
            dialog.Title,
            dialog.Description,
            null,
            dialog.Blocks,
            isPage: false,
            pluginService);
    }

    /// <summary>
    /// 执行一个按钮动作：
    /// - 配置了 dialog 的动作只打开浮窗；
    /// - 其余动作调用插件命令，并把输入框内容作为界面事件一并回传。
    /// </summary>
    public async Task ExecuteAsync(PluginUiAction action, IReadOnlyDictionary<string, string> values)
    {
        if (!string.IsNullOrWhiteSpace(action.Dialog))
        {
            DialogRequested?.Invoke(this, new PluginDialogRequestEventArgs(PluginId, action.Dialog));
            return;
        }

        if (!HasPermission(action.RequiredPermission))
        {
            StatusMessage = $"该操作需要权限“{action.RequiredPermission}”，请在设置 → 插件中授权。";
            return;
        }

        if (_pluginService is null)
        {
            StatusMessage = "插件服务不可用，无法执行该操作。";
            return;
        }

        if (string.IsNullOrWhiteSpace(action.Command))
        {
            StatusMessage = "该按钮未绑定命令。";
            return;
        }

        IsBusy = true;
        StatusMessage = null;
        try
        {
            var arguments = BuildArguments(action, values);
            var result = await _pluginService.ExecutePluginCommandAsync(PluginId, action.Command, arguments);

            if (!result.Success)
            {
                StatusMessage = result.Error ?? result.Message ?? "插件命令执行失败。";
                return;
            }

            var update = TryParseUpdate(result.UiJson);
            if (update is not null)
            {
                Apply(update);
            }

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                if (update is not null)
                {
                    // 同时返回了界面与文本时，文本作为状态提示，避免打断页面操作
                    StatusMessage = result.Message;
                }
                else
                {
                    ResultReady?.Invoke(this, new PluginCommandResultEventArgs
                    {
                        Title = action.Label,
                        PluginName = PluginName,
                        Content = result.Message
                    });
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>应用插件返回的界面更新。</summary>
    public void Apply(PluginUiUpdate update)
    {
        if (update.Blocks.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(update.Message)) StatusMessage = update.Message;
            return;
        }

        if (string.Equals(update.Mode, PluginUiUpdateModes.Append, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var block in update.Blocks) Blocks.Add(block);
        }
        else if (!string.IsNullOrWhiteSpace(update.TargetId))
        {
            var index = Blocks
                .Select((block, i) => (block, i))
                .FirstOrDefault(pair => string.Equals(pair.block.Id, update.TargetId, StringComparison.Ordinal))
                .i;

            var found = Blocks.Any(block => string.Equals(block.Id, update.TargetId, StringComparison.Ordinal));
            if (found)
            {
                Blocks.RemoveAt(index);
                for (var i = 0; i < update.Blocks.Count; i++) Blocks.Insert(index + i, update.Blocks[i]);
            }
            else
            {
                foreach (var block in update.Blocks) Blocks.Add(block);
            }
        }
        else
        {
            Blocks.Clear();
            foreach (var block in update.Blocks) Blocks.Add(block);
        }

        if (!string.IsNullOrWhiteSpace(update.Message)) StatusMessage = update.Message;
    }

    private string BuildArguments(PluginUiAction action, IReadOnlyDictionary<string, string> values)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // 动作上的静态参数先铺底，界面事件覆盖同名键，避免插件收到互相矛盾的参数
        if (!string.IsNullOrWhiteSpace(action.Arguments))
        {
            try
            {
                using var doc = JsonDocument.Parse(action.Arguments);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText());
                    }
                }
            }
            catch (JsonException)
            {
                // 参数写坏不阻断执行，插件仍能拿到界面事件
            }
        }

        payload["ui"] = new Dictionary<string, object?>
        {
            ["pluginId"] = PluginId,
            ["surfaceId"] = SurfaceId,
            ["actionId"] = action.Id,
            ["values"] = action.SubmitInputs ? values : new Dictionary<string, string>()
        };

        return JsonSerializer.Serialize(payload);
    }

    private static PluginUiUpdate? TryParseUpdate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginUiUpdate>(json, PluginManifestJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>插件请求打开浮窗。</summary>
public sealed class PluginDialogRequestEventArgs : EventArgs
{
    public PluginDialogRequestEventArgs(string pluginId, string dialogId)
    {
        PluginId = pluginId;
        DialogId = dialogId;
    }

    public string PluginId { get; }

    public string DialogId { get; }
}

/// <summary>功能菜单中的一条插件入口。</summary>
public sealed class PluginMenuEntry
{
    public string PluginId { get; init; } = string.Empty;
    public string PluginName { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string? Icon { get; init; }
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public string? Dialog { get; init; }
    public string? RequiredPermission { get; init; }
    public bool IsPermissionGranted { get; init; } = true;

    /// <summary>来源插件的完整界面声明，用于打开浮窗时复用同一份 bundle。</summary>
    public PluginUiBundle? Bundle { get; init; }
}
