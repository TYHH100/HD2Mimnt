using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModCreationViewModel : ObservableObject
{
    public ArchivePasswordsViewModel Passwords { get; }
    private readonly ModCreationService _service;
    private readonly IConfirmationService _confirmation;
    private readonly ImagePreviewService? _imagePreview;
    private Bitmap? _editingPreviewImage;
    [ObservableProperty] private ModCreationPreviewViewModel? _managerPreview;
    private string? _managerPreviewStamp;
    private string PreviewStamp => SourceDirectory + "\n" + JsonPreview + "\n" + MetadataPreview;
    public string ExportBaseName
    {
        get
        {
            var name = string.Concat(ModName.Trim().Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).TrimEnd('.', ' ');
            return string.IsNullOrWhiteSpace(name) ? "mod" : name;
        }
    }
    public string ArchiveFileName => ExportBaseName + "." + ArchiveExtension;
    private ModCreationDocument _document;
    private string _statusKey = "Creator.Ready";
    private object[] _statusArguments = [];
    private string? _errorKey;
    private CancellationTokenSource? _operation;
    private Task _completion = Task.CompletedTask;
    private bool _loading;
    private string? _initializedSource;
    private string? _automaticName;
    private string? _automaticIcon;
    private CancellationTokenSource? _sourceDelay;
    private Task _sourceCompletion = Task.CompletedTask;
    private bool _legacyInitialized;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveExtension))]
    private int _selectedArchiveFormatIndex;
    [ObservableProperty] private int _selectedCompressionIndex = 4;
    public string ArchiveExtension => SelectedArchiveFormatIndex == 1 ? "zip" : "7z";
    public ModArchiveCompression ArchiveCompression => SelectedCompressionIndex switch
    {
        0 => ModArchiveCompression.Store, 1 => ModArchiveCompression.Fast,
        2 => ModArchiveCompression.Normal, 3 => ModArchiveCompression.High, _ => ModArchiveCompression.Ultra
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLegacyManifest))]
    private bool _isV1Manifest = true;
    public bool IsLegacyManifest { get => !IsV1Manifest; set => IsV1Manifest = !value; }
    [ObservableProperty] private string? _selectedLegacyOption;
    public ObservableCollection<string> LegacyOptions { get; } = [];
    [ObservableProperty] private string _modName = string.Empty;
    [ObservableProperty] private string _author = string.Empty;
    [ObservableProperty] private string _authorUrl = string.Empty;
    [ObservableProperty] private string _modVersion = "1.0.0";
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _iconPath = string.Empty;
    [ObservableProperty] private string _sourceDirectory = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private ModCreationOptionViewModel? _selectedOption;
    [ObservableProperty] private string _jsonPreview = string.Empty;
    [ObservableProperty] private string _metadataPreview = string.Empty;
    public ObservableCollection<ModCreationOptionViewModel> Options { get; } = [];
    public bool CanEdit => !IsBusy;
    public event EventHandler? SortCancellationRequested;
    public void CancelSorting() => SortCancellationRequested?.Invoke(this, EventArgs.Empty);
    public string StatusMessage => UiLocalization.Current.Format(_statusKey,
        _errorKey == null ? _statusArguments : [UiLocalization.Current[_errorKey]]);

    public ModCreationViewModel(ModCreationService service, IConfirmationService confirmation, ImagePreviewService? imagePreview = null, SettingsService? settingsService = null)
    {
        Passwords = new(settingsService);
        _imagePreview = imagePreview;
        _service = service; _confirmation = confirmation; _document = service.Create();
        LegacyOptions.CollectionChanged += (_, _) => Changed();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ModName) or nameof(Author) or nameof(AuthorUrl) or nameof(ModVersion) or nameof(Description)
                or nameof(IconPath) or nameof(SourceDirectory)) Changed();
        };
        RefreshPreview();
    }

    partial void OnIsV1ManifestChanged(bool value)
    {
        CancelSorting();
        if (_loading) return;
        if (!value) InitializeLegacyOptions();
        Changed();
    }

    private void InitializeLegacyOptions()
    {
        if (_legacyInitialized || Options.Count == 0) return;
        _legacyInitialized = true;
        foreach (var path in Options.Where(option => option.HasIncludeMapping)
                     .SelectMany(option => ModCreationOptionViewModel.ParsePaths(option.IncludePaths))
                     .Where(path => path != ".").Distinct(StringComparer.OrdinalIgnoreCase))
            LegacyOptions.Add(path);
        SelectedLegacyOption = LegacyOptions.FirstOrDefault();
    }

    public void AddLegacyDirectories(IEnumerable<ModCreationDirectory> directories)
    {
        if (IsBusy || IsV1Manifest) return;
        _legacyInitialized = true;
        foreach (var directory in directories)
            if (!LegacyOptions.Contains(directory.RelativePath, StringComparer.OrdinalIgnoreCase))
                LegacyOptions.Add(directory.RelativePath);
        SelectedLegacyOption ??= LegacyOptions.FirstOrDefault();
    }

    [RelayCommand]
    private void RemoveLegacyOption()
    {
        if (IsBusy || SelectedLegacyOption is not { } selected) return;
        _legacyInitialized = true;
        LegacyOptions.Remove(selected);
        SelectedLegacyOption = LegacyOptions.FirstOrDefault();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEdit));
    partial void OnSourceDirectoryChanged(string value)
    {
        _sourceDelay?.Cancel();
        if (_loading || string.IsNullOrWhiteSpace(value)) return;
        var delay = new CancellationTokenSource();
        _sourceDelay = delay;
        _sourceCompletion = InitializeAfterTypingAsync(delay);
    }

    private async Task InitializeAfterTypingAsync(CancellationTokenSource delay)
    {
        try { await Task.Delay(400, delay.Token); await InitializeSourceAsync(); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_sourceDelay, delay)) _sourceDelay = null;
            delay.Dispose();
        }
    }

    private void Changed()
    {
        if (_loading) return;
        IsDirty = true;
        RefreshPreview();
    }

    private ModCreationDocument Snapshot()
    {
        _document.SourceDirectory = SourceDirectory;
        var manifest = _document.Manifest;
        manifest.Name = ModName; manifest.Author = Author; manifest.Version = ModVersion;
        manifest.AuthorUrl = AuthorUrl;
        manifest.Description = Description; manifest.IconPath = IconPath;
        manifest.ManifestVersion = IsV1Manifest ? ManifestVersion.V1 : ManifestVersion.Legacy;
        manifest.LegacyOptions = LegacyOptions.ToList();
        manifest.Options = Options.Select(option => option.ToModel()).ToList();
        return _document;
    }

    private void RefreshPreview()
    {
        var document = Snapshot();
        JsonPreview = _service.Preview(document);
        MetadataPreview = _service.PreviewMetadata(document);
    }
    private void SetStatus(string key, params object[] arguments)
    { _statusKey = key; _statusArguments = arguments; _errorKey = null; OnPropertyChanged(nameof(StatusMessage)); }

    public void ShowError(Exception error)
    {
        _statusKey = "Creator.Failed"; _statusArguments = [error.Message];
        _errorKey = error.Message.StartsWith("Creator.", StringComparison.Ordinal)
            || error.Message.StartsWith("ArchivePasswords.", StringComparison.Ordinal) ? error.Message : null;
        OnPropertyChanged(nameof(StatusMessage));
    }

    public async Task<bool> ConfirmDiscardAsync() => !IsDirty || await _confirmation.ConfirmAsync(
        UiLocalization.Current["Creator.Title"], UiLocalization.Current["Creator.Discard"]);

    [RelayCommand]
    private async Task NewAsync()
    {
        if (IsBusy || !await ConfirmDiscardAsync()) return;
        Apply(_service.Create()); SetStatus("Creator.Ready");
    }

    private void Apply(ModCreationDocument document)
    {
        CloseEditingImagePreview();
        var oldPreview = ManagerPreview; ManagerPreview = null; oldPreview?.Dispose();
        _loading = true;
        try
        {
            foreach (var option in Options) option.PropertyChanged -= OptionChanged;
            Options.Clear(); _document = document;
            IsV1Manifest = document.Manifest.ManifestVersion == ManifestVersion.V1;
            _legacyInitialized = !IsV1Manifest;
            LegacyOptions.Clear();
            foreach (var path in document.Manifest.LegacyOptions ?? []) LegacyOptions.Add(path);
            SelectedLegacyOption = LegacyOptions.FirstOrDefault();
            ModName = document.Manifest.Name; Author = document.Manifest.Author; ModVersion = document.Manifest.Version;
            AuthorUrl = document.Manifest.AuthorUrl ?? string.Empty;
            Description = document.Manifest.Description; IconPath = document.Manifest.IconPath ?? "";
            SourceDirectory = document.SourceDirectory;
            _initializedSource = document.SourceDirectory;
            _automaticName = null; _automaticIcon = null;
            foreach (var option in document.Manifest.Options ?? []) Add(new(option));
            if (!IsV1Manifest)
                foreach (var path in LegacyOptions)
                    Add(new(new ModOption { Name = path, Include = [path] }));
            SelectedOption = Options.FirstOrDefault(); IsDirty = false;
        }
        finally { _loading = false; RefreshPreview(); }
    }

    private void Add(ModCreationOptionViewModel option)
    { option.PropertyChanged += OptionChanged; Options.Add(option); }
    private void OptionChanged(object? sender, PropertyChangedEventArgs args) => Changed();

    [RelayCommand]
    private void AddOption()
    {
        if (IsBusy) return;
        var option = new ModCreationOptionViewModel(new ModOption { DefaultEnabled = true, Include = [] });
        Add(option); SelectedOption = option; Changed();
    }

    [RelayCommand]
    private void RemoveOption()
    {
        if (IsBusy || SelectedOption is not { } option) return;
        option.PropertyChanged -= OptionChanged; Options.Remove(option);
        SelectedOption = Options.FirstOrDefault(); Changed();
    }

    public bool MoveOption(ModCreationOptionViewModel option, int insertionSlot)
    {
        if (IsBusy) return false;
        var selected = SelectedOption;
        if (!MoveAtSlot(Options, option, insertionSlot)) return false;
        SelectedOption = selected;
        Changed();
        return true;
    }

    public bool MoveSubOption(ModCreationOptionViewModel parent, ModCreationSubOptionViewModel option, int insertionSlot)
        => !IsBusy && Options.Contains(parent) && parent.MoveSubOption(option, insertionSlot);

    internal static bool MoveAtSlot<T>(ObservableCollection<T> items, T item, int insertionSlot)
    {
        var current = items.IndexOf(item);
        if (current < 0 || insertionSlot < 0 || insertionSlot > items.Count) return false;
        var destination = insertionSlot > current ? insertionSlot - 1 : insertionSlot;
        if (destination == current) return false;
        items.Move(current, destination);
        return true;
    }

    public string RelativePath(string selected) => _service.GetRelativePath(SourceDirectory, selected);

    public Task InitializeSourceAsync()
    {
        _sourceDelay?.Cancel();
        return string.IsNullOrWhiteSpace(SourceDirectory)
            || string.Equals(_initializedSource, SourceDirectory, StringComparison.OrdinalIgnoreCase)
            ? Task.CompletedTask : RunAsync(async token =>
        {
            var source = SourceDirectory;
            var directory = await _service.ReadDirectoryAsync(source, token);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(SourceDirectory, source, StringComparison.Ordinal)) return;
            _loading = true;
            try
            {
                if (string.IsNullOrWhiteSpace(ModName) || ModName == _automaticName)
                    ModName = _automaticName = directory.Name;
                if (string.IsNullOrWhiteSpace(IconPath) || IconPath == _automaticIcon)
                    IconPath = _automaticIcon = directory.Image ?? string.Empty;
                if (Options.Count == 0)
                    foreach (var option in _service.CreateDirectoryOptions(directory, UiLocalization.Current["Creator.BaseFiles"])) Add(new(option));
                if (!IsV1Manifest) InitializeLegacyOptions();
                SelectedOption ??= Options.FirstOrDefault();
                _initializedSource = source;
            }
            finally { _loading = false; }
            Changed(); SetStatus("Creator.Ready");
        });
    }

    public async Task<ModCreationDirectory?> ReadDirectoriesAsync()
    {
        ModCreationDirectory? result = null;
        await RunAsync(async token =>
        {
            if (string.IsNullOrWhiteSpace(SourceDirectory)) throw new InvalidDataException("Creator.DirectoryRequired");
            result = await _service.ReadDirectoryAsync(SourceDirectory, token);
            SetStatus("Creator.Ready");
        });
        return result;
    }

    public void ApplyDirectories(object target, IEnumerable<ModCreationDirectory> selected)
    {
        if (IsBusy) return;
        var directories = selected.ToList();
        var paths = string.Join(Environment.NewLine, directories.Select(directory => directory.RelativePath));
        var first = directories.FirstOrDefault();
        switch (target)
        {
            case ModCreationOptionViewModel option when Options.Contains(option):
                option.HasIncludeMapping = true; option.IncludePaths = paths;
                if (string.IsNullOrWhiteSpace(option.Name) && first != null) option.Name = first.Name;
                if (string.IsNullOrWhiteSpace(option.ImagePath)) option.ImagePath = first?.Image ?? "";
                break;
            case ModCreationSubOptionViewModel sub when Options.Any(option => option.SubOptions.Contains(sub)):
                sub.HasIncludeMapping = true; sub.IncludePaths = paths;
                if (string.IsNullOrWhiteSpace(sub.Name) && first != null) sub.Name = first.Name;
                if (string.IsNullOrWhiteSpace(sub.ImagePath)) sub.ImagePath = first?.Image ?? "";
                break;
        }
    }
    public Task OpenAsync(string path) => RunAsync(async token =>
    { Apply(await _service.LoadAsync(path, token)); SetStatus("Creator.Opened", path); });
    public Task SaveAsync(string path) => RunAsync(async token =>
    { await _service.SaveAsync(Snapshot(), path, token); IsDirty = false; SetStatus("Creator.Saved", path); });
    public Task ExportAsync(string path) => RunAsync(async token =>
    { await _service.ExportArchiveAsync(Snapshot(), path, ArchiveCompression, Passwords.GetExportPassword(), token); IsDirty = false; SetStatus("Creator.Exported", path); });

    public Task RefreshManagerPreviewAsync() => RunAsync(async token =>
    {
        var preview = await CreateManagerPreviewAsync(token);
        var old = ManagerPreview; ManagerPreview = preview; old?.Dispose();
        _managerPreviewStamp = PreviewStamp;
        SetStatus("Creator.Ready");
    });

    public Task PreviewImageAsync(string? path, string? title) => RunAsync(async token =>
    {
        if (_imagePreview == null) return;
        var bytes = await _service.ReadPreviewImageAsync(SourceDirectory, path, token)
            ?? throw new InvalidDataException("Creator.ImageUnavailable");
        Bitmap? image = await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }, token);
        try
        {
            token.ThrowIfCancellationRequested();
            _imagePreview.Show(image, title, ownsImage: true);
            _editingPreviewImage = image;
            image = null;
            SetStatus("Creator.Ready");
        }
        finally { image?.Dispose(); }
    });

    private void CloseEditingImagePreview()
    {
        if (_editingPreviewImage != null && ReferenceEquals(_imagePreview?.Image, _editingPreviewImage))
            _imagePreview!.Close();
        _editingPreviewImage = null;
    }

    private async Task<ModCreationPreviewViewModel> CreateManagerPreviewAsync(CancellationToken token)
    {
        var preview = new ModCreationPreviewViewModel(Snapshot().Manifest, _imagePreview);
        try { await preview.LoadImagesAsync(_service, SourceDirectory, token); return preview; }
        catch { preview.Dispose(); throw; }
    }

    public Task ExportScreenshotAsync(string path, Func<ModCreationPreviewViewModel, CancellationToken, Task<byte[]>> render)
        => RunAsync(async token =>
        {
            var current = ManagerPreview != null && _managerPreviewStamp == PreviewStamp;
            var preview = current ? ManagerPreview! : await CreateManagerPreviewAsync(token);
            try
            {
                var png = await render(preview, token);
                await _service.SavePreviewImageAsync(path, png, token);
                SetStatus("Creator.Exported", path);
            }
            finally { if (!current) preview.Dispose(); }
        });

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return _completion;
        IsBusy = true; _operation = new();
        _completion = ExecuteAsync(action, _operation); return _completion;
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancellation)
    {
        try { SetStatus("Creator.Working"); await action(cancellation.Token); }
        catch (OperationCanceledException) { SetStatus("Creator.Cancelled"); }
        catch (Exception error) { ShowError(error); }
        finally { _operation = null; cancellation.Dispose(); IsBusy = false; }
    }
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    public async Task StopAsync()
    { CancelSorting(); _sourceDelay?.Cancel(); _operation?.Cancel(); await _sourceCompletion; await _completion;
        CloseEditingImagePreview();
        var preview = ManagerPreview; ManagerPreview = null; preview?.Dispose(); }
}

public partial class ModCreationOptionViewModel : ObservableObject
{
    private readonly ModOption _model;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _imagePath = string.Empty;
    [ObservableProperty] private string _includePaths = string.Empty;
    [ObservableProperty] private bool _hasIncludeMapping;
    [ObservableProperty] private bool? _defaultEnabled;
    public ObservableCollection<ModCreationSubOptionViewModel> SubOptions { get; } = [];

    public ModCreationOptionViewModel(ModOption model)
    {
        _model = model; Name = model.Name; Description = model.Description; ImagePath = model.Image ?? "";
        HasIncludeMapping = model.Include != null; IncludePaths = string.Join(Environment.NewLine, model.Include ?? []);
        DefaultEnabled = model.DefaultEnabled;
        foreach (var child in model.SubOptions ?? []) Add(new(child));
    }
    private void Add(ModCreationSubOptionViewModel child)
    { child.PropertyChanged += ChildChanged; SubOptions.Add(child); }
    private void ChildChanged(object? sender, PropertyChangedEventArgs args) => OnPropertyChanged(nameof(SubOptions));
    [RelayCommand] private void AddSubOption()
    { Add(new(new ModSubOption { Include = [] })); OnPropertyChanged(nameof(SubOptions)); }
    [RelayCommand] private void RemoveSubOption(ModCreationSubOptionViewModel child)
    { child.PropertyChanged -= ChildChanged; SubOptions.Remove(child); OnPropertyChanged(nameof(SubOptions)); }
    internal bool MoveSubOption(ModCreationSubOptionViewModel child, int insertionSlot)
    {
        if (!ModCreationViewModel.MoveAtSlot(SubOptions, child, insertionSlot)) return false;
        OnPropertyChanged(nameof(SubOptions));
        return true;
    }
    public ModOption ToModel()
    {
        _model.Name = Name; _model.Description = Description; _model.Image = ImagePath; _model.DefaultEnabled = DefaultEnabled;
        _model.Include = HasIncludeMapping ? ParsePaths(IncludePaths) : null;
        _model.SubOptions = SubOptions.Select(child => child.ToModel()).ToList(); return _model;
    }
    internal static List<string> ParsePaths(string paths) => paths.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path.Trim()).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public partial class ModCreationSubOptionViewModel : ObservableObject
{
    private readonly ModSubOption _model;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _imagePath = string.Empty;
    [ObservableProperty] private string _includePaths = string.Empty;
    [ObservableProperty] private bool _hasIncludeMapping;
    public ModCreationSubOptionViewModel(ModSubOption model)
    {
        _model = model; Name = model.Name; Description = model.Description; ImagePath = model.Image ?? "";
        HasIncludeMapping = model.Include != null; IncludePaths = string.Join(Environment.NewLine, model.Include ?? []);
    }
    public ModSubOption ToModel()
    {
        _model.Name = Name; _model.Description = Description; _model.Image = ImagePath;
        _model.Include = HasIncludeMapping ? ModCreationOptionViewModel.ParsePaths(IncludePaths) : null; return _model;
    }
}
