using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public partial class ArchivePasswordsViewModel : ObservableObject
{
    private readonly ArchivePasswordService? _service;
    private Guid? _defaultId;
    public ObservableCollection<ArchivePasswordEntry> Entries { get; } = [];
    [ObservableProperty] private ArchivePasswordEntry? _selected;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _makeDefault;
    [ObservableProperty] private bool _usePassword;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _defaultName = "";
    public bool Available => _service != null;

    public ArchivePasswordsViewModel(SettingsService? settings)
    {
        _service = settings == null ? null : new ArchivePasswordService(settings);
        Reload(selectDefault: true);
    }

    public void Reload(bool selectDefault = false)
    {
        if (_service == null) return;
        try
        {
            var library = _service.Read();
            var selectedId = selectDefault ? library.DefaultId : Selected?.Id;
            _defaultId = library.DefaultId;
            Entries.Clear();
            foreach (var entry in library.Entries) Entries.Add(entry);
            DefaultName = Entries.FirstOrDefault(e => e.Id == _defaultId)?.Name ?? "";
            Selected = Entries.FirstOrDefault(e => e.Id == selectedId);
            if (selectDefault) UsePassword = Selected != null;
            Error = "";
        }
        catch { Error = UiLocalization.Current["ArchivePasswords.ReadFailed"]; }
    }

    partial void OnSelectedChanged(ArchivePasswordEntry? value)
    {
        Name = value?.Name ?? "";
        Password = value?.Password ?? "";
        MakeDefault = value != null && value.Id == _defaultId;
        if (value != null) UsePassword = true;
    }

    public string? GetExportPassword()
    {
        if (_service != null)
        {
            var current = _service.Read();
            if (current.DefaultId != _defaultId || current.Entries.FirstOrDefault(e => e.Id == current.DefaultId)
                != Entries.FirstOrDefault(e => e.Id == _defaultId)) Reload(selectDefault: true);
        }
        if (!UsePassword) return null;
        if (string.IsNullOrEmpty(Password)) throw new InvalidDataException("ArchivePasswords.PasswordRequired");
        return Password;
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        Name = Password = "";
        MakeDefault = false;
    }

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        var saved = await _service!.SaveAsync(Name, Password, MakeDefault);
        Reload();
        Selected = Entries.First(e => e.Id == saved.Id);
    });

    [RelayCommand]
    private Task DeleteAsync() => RunAsync(async () =>
    {
        if (Selected == null) return;
        await _service!.DeleteAsync(Selected.Id);
        Reload(); New();
    });

    [RelayCommand]
    private Task ClearDefaultAsync() => RunAsync(async () =>
    {
        await _service!.SetDefaultAsync(null);
        Reload(); MakeDefault = false;
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy || _service == null) return;
        IsBusy = true;
        try { await action(); Error = ""; }
        catch (InvalidDataException ex) { Error = UiLocalization.Current[ex.Message]; }
        catch { Error = UiLocalization.Current["ArchivePasswords.SaveFailed"]; }
        finally { IsBusy = false; }
    }
}
