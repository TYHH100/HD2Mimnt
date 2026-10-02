using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// ViewModel for external tools management
/// </summary>
public partial class ExternalToolsViewModel : ObservableObject
{
    private readonly IExternalToolService _toolService;

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

    public ExternalToolsViewModel(IExternalToolService toolService)
    {
        _toolService = toolService;
    }

    [RelayCommand]
    private async Task InitializeAsync()
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
    private async Task AddToolAsync()
    {
        // TODO: Open add tool dialog
        StatusMessage = "Add tool dialog not implemented yet";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task EditToolAsync(ExternalToolItemViewModel? tool)
    {
        if (tool == null) return;

        // TODO: Open edit tool dialog
        StatusMessage = $"Edit dialog for '{tool.Name}' not implemented yet";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteToolAsync(ExternalToolItemViewModel? tool)
    {
        if (tool == null) return;

        // TODO: Show confirmation dialog

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
