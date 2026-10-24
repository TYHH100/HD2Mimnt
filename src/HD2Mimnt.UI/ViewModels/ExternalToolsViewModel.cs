using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using System.Collections.ObjectModel;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// ViewModel for external tools management
/// </summary>
public partial class ExternalToolsViewModel : ObservableObject
{
    private readonly IExternalToolService _toolService;
    private readonly IConfirmationService _confirmationService;

    [ObservableProperty]
    private ObservableCollection<ExternalToolItemViewModel> _tools = new();

    [ObservableProperty]
    private ExternalToolItemViewModel? _selectedTool;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalTools;

    [ObservableProperty]
    private int _enabledTools;

    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editId = string.Empty;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editDescription = string.Empty;
    [ObservableProperty] private string _editExecutablePath = string.Empty;
    [ObservableProperty] private string _editArguments = string.Empty;
    [ObservableProperty] private string _editWorkingDirectory = string.Empty;
    [ObservableProperty] private string _editSupportedExtensions = string.Empty;
    [ObservableProperty] private bool _editIsEnabled = true;

    public ExternalToolsViewModel(IExternalToolService toolService, IConfirmationService confirmationService)
    {
        _toolService = toolService;
        _confirmationService = confirmationService;
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        await LoadToolsAsync();
    }

    [RelayCommand]
    private async Task LoadToolsAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading tools...";

        try
        {
            var tools = await _toolService.GetAllToolsAsync();

            Tools.Clear();
            foreach (var tool in tools)
            {
                Tools.Add(new ExternalToolItemViewModel(tool));
            }

            UpdateStatistics();
            StatusMessage = $"Loaded {tools.Count} tool(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading tools: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void AddTool()
    {
        IsEditing = false;
        EditId = Guid.NewGuid().ToString("N");
        EditName = string.Empty;
        EditDescription = string.Empty;
        EditExecutablePath = string.Empty;
        EditArguments = string.Empty;
        EditWorkingDirectory = string.Empty;
        EditSupportedExtensions = string.Empty;
        EditIsEnabled = true;
        IsEditorOpen = true;
        StatusMessage = "请输入外部工具信息";
    }

    [RelayCommand]
    private void EditTool(ExternalToolItemViewModel? tool)
    {
        if (tool == null) return;

        IsEditing = true;
        EditId = tool.Id;
        EditName = tool.Name;
        EditDescription = tool.Description ?? string.Empty;
        EditExecutablePath = tool.ExecutablePath;
        EditArguments = tool.Arguments ?? string.Empty;
        EditWorkingDirectory = tool.WorkingDirectory ?? string.Empty;
        EditSupportedExtensions = tool.SupportedExtensions ?? string.Empty;
        EditIsEnabled = tool.IsEnabled;
        IsEditorOpen = true;
        StatusMessage = $"正在编辑“{tool.Name}”";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorOpen = false;
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private async Task SaveToolAsync()
    {
        var tool = new ExternalTool
        {
            Id = EditId.Trim(),
            Name = EditName.Trim(),
            Description = NullIfEmpty(EditDescription),
            ExecutablePath = EditExecutablePath.Trim(),
            Arguments = NullIfEmpty(EditArguments),
            WorkingDirectory = NullIfEmpty(EditWorkingDirectory),
            SupportedExtensions = NullIfEmpty(EditSupportedExtensions),
            IsEnabled = EditIsEnabled
        };

        var validation = await _toolService.ValidateToolAsync(tool);
        if (!validation.IsValid)
        {
            StatusMessage = $"校验失败：{string.Join("；", validation.Errors)}";
            return;
        }

        var result = IsEditing
            ? await _toolService.UpdateToolAsync(tool)
            : await _toolService.RegisterToolAsync(tool);
        if (!result.Success)
        {
            StatusMessage = result.Error ?? "保存外部工具失败";
            return;
        }

        await LoadToolsAsync();
        IsEditorOpen = false;
        StatusMessage = IsEditing ? "外部工具已更新" : "外部工具已添加";
    }

    [RelayCommand]
    private async Task DeleteToolAsync(ExternalToolItemViewModel? tool)
    {
        if (tool == null) return;

        if (!await _confirmationService.ConfirmAsync("删除外部工具", $"确定删除“{tool.Name}”吗？"))
        {
            return;
        }

        StatusMessage = $"Deleting tool '{tool.Name}'...";

        var result = await _toolService.DeleteToolAsync(tool.Id);
        if (result.Success)
        {
            Tools.Remove(tool);
            UpdateStatistics();
            StatusMessage = $"Tool '{tool.Name}' deleted";
        }
        else
        {
            StatusMessage = $"Error: {result.Error}";
        }
    }

    [RelayCommand]
    private async Task ToggleToolEnabledAsync(ExternalToolItemViewModel? tool)
    {
        if (tool == null) return;

        var newState = !tool.IsEnabled;
        var result = await _toolService.SetToolEnabledAsync(tool.Id, newState);

        if (result.Success)
        {
            tool.IsEnabled = newState;
            UpdateStatistics();
            StatusMessage = $"Tool '{tool.Name}' {(newState ? "enabled" : "disabled")}";
        }
        else
        {
            StatusMessage = $"Error: {result.Error}";
        }
    }

    [RelayCommand]
    private async Task LaunchToolWithFileAsync((string toolId, string filePath) args)
    {
        StatusMessage = $"Launching tool...";

        var result = await _toolService.LaunchToolAsync(args.toolId, args.filePath);
        if (result.Success)
        {
            StatusMessage = result.Message ?? "Tool launched";
        }
        else
        {
            StatusMessage = $"Error: {result.Error}";
        }
    }

    public async Task LaunchToolAsync(string toolId, string filePath, CancellationToken cancellationToken = default)
    {
        StatusMessage = "正在启动外部工具...";
        var result = await _toolService.LaunchToolAsync(toolId, filePath, cancellationToken);
        StatusMessage = result.Success
            ? result.Message ?? "外部工具已启动"
            : $"启动失败：{result.Error}";
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadToolsAsync();
    }

    private void UpdateStatistics()
    {
        TotalTools = Tools.Count;
        EnabledTools = Tools.Count(t => t.IsEnabled);
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// ViewModel for individual external tool item
/// </summary>
public partial class ExternalToolItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private string _executablePath;

    [ObservableProperty]
    private string? _arguments;

    [ObservableProperty]
    private string? _workingDirectory;

    [ObservableProperty]
    private string? _icon;

    [ObservableProperty]
    private string? _supportedExtensions;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private int _displayOrder;

    [ObservableProperty]
    private DateTime _createdAt;

    [ObservableProperty]
    private DateTime? _modifiedAt;

    public ExternalToolItemViewModel(ExternalTool tool)
    {
        _id = tool.Id;
        _name = tool.Name;
        _description = tool.Description;
        _executablePath = tool.ExecutablePath;
        _arguments = tool.Arguments;
        _workingDirectory = tool.WorkingDirectory;
        _icon = tool.Icon;
        _supportedExtensions = tool.SupportedExtensions;
        _isEnabled = tool.IsEnabled;
        _displayOrder = tool.DisplayOrder;
        _createdAt = tool.CreatedAt;
        _modifiedAt = tool.ModifiedAt;
    }

    public ExternalTool ToEntity()
    {
        return new ExternalTool
        {
            Id = Id,
            Name = Name,
            Description = Description,
            ExecutablePath = ExecutablePath,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            Icon = Icon,
            SupportedExtensions = SupportedExtensions,
            IsEnabled = IsEnabled,
            DisplayOrder = DisplayOrder,
            CreatedAt = CreatedAt,
            ModifiedAt = ModifiedAt
        };
    }
}
