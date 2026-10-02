using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using System.Collections.ObjectModel;
using System.Linq;

namespace HD2Mimnt.UI.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly ModService _modService;

    [ObservableProperty]
    private ObservableCollection<ModViewModel> _mods = new();

    [ObservableProperty]
    private ModViewModel? _selectedMod;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ModStatistics? _statistics;

    [ObservableProperty]
    private ObservableCollection<string> _availableTags = new();

    [ObservableProperty]
    private ModCategory _selectedCategory = ModCategory.Unknown;

    [ObservableProperty]
    private string? _selectedTag;

    public LibraryViewModel(ModService modService)
    {
        _modService = modService;
    }

    public async Task InitializeAsync()
    {
        await LoadModsAsync();
        await LoadStatisticsAsync();
        await LoadTagsAsync();
    }

    public async Task LoadModsAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading mods...";
        try
        {
            var mods = await _modService.GetAllModsAsync();
            Mods.Clear();
            foreach (var mod in mods)
            {
                Mods.Add(new ModViewModel(mod));
            }
            StatusMessage = $"Loaded {Mods.Count} mods";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load mods: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadStatisticsAsync()
    {
        try
        {
            Statistics = await _modService.GetStatisticsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load statistics: {ex.Message}";
        }
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            var tags = await _modService.GetAllTagsAsync();
            AvailableTags.Clear();
            foreach (var tag in tags)
            {
                AvailableTags.Add(tag);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load tags: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            await LoadModsAsync();
            return;
        }

        IsLoading = true;
        StatusMessage = "Searching...";
        try
        {
            var mods = await _modService.SearchModsAsync(SearchText);
            Mods.Clear();
            foreach (var mod in mods)
            {
                Mods.Add(new ModViewModel(mod));
            }
            StatusMessage = $"Found {Mods.Count} mods";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task FilterByCategoryAsync()
    {
        if (SelectedCategory == ModCategory.Unknown)
        {
            await LoadModsAsync();
            return;
        }

        IsLoading = true;
        StatusMessage = $"Filtering by {SelectedCategory}...";
        try
        {
            var mods = await _modService.GetModsByCategoryAsync(SelectedCategory);
            Mods.Clear();
            foreach (var mod in mods)
            {
                Mods.Add(new ModViewModel(mod));
            }
            StatusMessage = $"Found {Mods.Count} mods in {SelectedCategory}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Filter failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task FilterByTagAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedTag))
        {
            await LoadModsAsync();
            return;
        }

        IsLoading = true;
        StatusMessage = $"Filtering by tag '{SelectedTag}'...";
        try
        {
            var mods = await _modService.GetModsByTagAsync(SelectedTag);
            Mods.Clear();
            foreach (var mod in mods)
            {
                Mods.Add(new ModViewModel(mod));
            }
            StatusMessage = $"Found {Mods.Count} mods with tag '{SelectedTag}'";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Filter failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ImportModAsync()
    {
        // TODO: 实现文件夹选择对话框
        StatusMessage = "Import function coming soon...";
    }

    [RelayCommand]
    private async Task DeleteModAsync(ModViewModel? mod)
    {
        if (mod == null) return;

        // TODO: 添加确认对话框

        IsLoading = true;
        StatusMessage = $"Deleting {mod.Name}...";
        try
        {
            var success = await _modService.DeleteModAsync(mod.Id);
            if (success)
            {
                Mods.Remove(mod);
                StatusMessage = $"Deleted {mod.Name}";
                await LoadStatisticsAsync();
            }
            else
            {
                StatusMessage = $"Failed to delete {mod.Name}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadModsAsync();
        await LoadStatisticsAsync();
        await LoadTagsAsync();
    }
}

public partial class ModViewModel : ObservableObject
{
    private readonly Mod _mod;

    public Guid Id => _mod.Id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _version;

    [ObservableProperty]
    private string _author;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private ModCategory _category;

    [ObservableProperty]
    private ModStatus _status;

    [ObservableProperty]
    private ObservableCollection<string> _tags;

    [ObservableProperty]
    private long _size;

    [ObservableProperty]
    private ModSource _source;

    [ObservableProperty]
    private bool _containsScripts;

    public string SizeFormatted => FormatSize(Size);

    public string CategoryDisplay => Category.ToString();

    public string SourceDisplay => Source.ToString();

    public ModViewModel(Mod mod)
    {
        _mod = mod;
        _name = mod.Name;
        _version = mod.Version;
        _author = mod.Author;
        _description = mod.Description;
        _category = mod.Category;
        _status = mod.Status;
        _tags = new ObservableCollection<string>(mod.Tags);
        _size = mod.Size;
        _source = mod.Source;
        _containsScripts = mod.ContainsScripts;
    }

    public Mod ToModel()
    {
        _mod.Name = Name;
        _mod.Version = Version;
        _mod.Author = Author;
        _mod.Description = Description;
        _mod.Category = Category;
        _mod.Status = Status;
        _mod.Tags = Tags.ToList();
        return _mod;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

