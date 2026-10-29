using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public sealed record ModelPreviewScene(IReadOnlyList<ModelPreviewMesh> Meshes,
    IReadOnlyDictionary<ulong, TexturePreviewData> Textures, Matrix4x4 PresentationTransform);

public sealed record ModelPreviewResolutionOption(string Display, int Value);

public partial class ModelPreviewViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ModelPreviewService _service;
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private int _generation;
    private ModelPreviewSource? _source;
    private ModelPreviewResult? _result;
    private IReadOnlyDictionary<ulong, TexturePreviewData> _textures = new Dictionary<ulong, TexturePreviewData>();
    private bool _updatingRows;
    private Matrix4x4 _presentationTransform = Matrix4x4.Identity;
    private int _missingTextureCount;

    public ModelPreviewViewModel(ModelPreviewService service)
    {
        _service = service;
        UiLocalization.Current.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args) => OnPropertyChanged(nameof(Summary));

    public async ValueTask DisposeAsync()
    {
        UiLocalization.Current.PropertyChanged -= OnLanguageChanged;
        await StopAsync();
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private ModelPreviewScene? _scene;
    [ObservableProperty] private bool _wireframe;
    [ObservableProperty] private bool _useTextures = true;
    [ObservableProperty] private bool _useAlpha;
    [ObservableProperty] private bool _showGrid = true;
    [ObservableProperty] private bool _showStockyBody;
    [ObservableProperty] private bool _showFilteredMeshes;
    [ObservableProperty] private bool _isolateSelectedMesh;
    [ObservableProperty] private ModelPreviewMeshRow? _selectedMesh;
    [ObservableProperty] private int _textureResolution = 1024;
    [ObservableProperty] private int _legacySelectedIndex;
    public IReadOnlyList<ModelPreviewResolutionOption> TextureResolutions { get; } =
    [
        new("512", 512), new("1024", 1024), new("2048", 2048),
        new("Original", ModelPreviewService.OriginalTextureResolution)
    ];
    public ObservableCollection<ModelPreviewOptionRow> Options { get; } = [];
    public ObservableCollection<ModelPreviewMeshRow> Meshes { get; } = [];
    public ObservableCollection<string> LegacyOptions { get; } = [];
    public bool HasLegacyOptions => LegacyOptions.Count > 0;
    public bool HasOptions => HasLegacyOptions || Options.Count > 0;
    public bool HasError => Error.Length > 0;
    public bool HasBodyShapes => _result is not null && ModelPreviewBodyShapeSelection.GetSwitchableSlots(_result.Meshes).Count > 0;
    public Task Completion => _operation;
    public string Summary => _result is null ? UiLocalization.Current[IsBusy ? "ModelPreview.Loading" : "ModelPreview.Empty"]
        : UiLocalization.Current.Format("ModelPreview.Summary", Scene?.Meshes.Count ?? 0,
            Scene?.Meshes.Sum(mesh => mesh.TriangleCount) ?? 0, _textures.Count, _result.SkippedStreams)
            + (_missingTextureCount > 0 ? " · " + UiLocalization.Current.Format("ModelPreview.MissingTextures", _missingTextureCount) : string.Empty);

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(Summary));
    partial void OnShowStockyBodyChanged(bool value) => RebuildScene();
    partial void OnShowFilteredMeshesChanged(bool value) => RebuildScene();
    partial void OnIsolateSelectedMeshChanged(bool value) => RebuildScene();
    partial void OnSelectedMeshChanged(ModelPreviewMeshRow? value) => RebuildScene();

    public Task OpenAsync(Guid modId, Guid? profileId = null)
    {
        IsOpen = true;
        ClearContent();
        return RunAsync(async token =>
        {
            var source = await _service.GetSourceAsync(modId, profileId, token);
            token.ThrowIfCancellationRequested();
            _source = source;
            Title = source.Name;
            SetOptions(source);
            await LoadAsync(token);
        });
    }

    private void SetOptions(ModelPreviewSource source)
    {
        Options.Clear(); LegacyOptions.Clear();
        if (source.Manifest?.ManifestVersion == ManifestVersion.V1 && source.Manifest.Options is { } options)
        {
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var group = source.Manifest.OptionGroups?.FirstOrDefault(group => group.Id == option.GroupId)?.Name;
                Options.Add(new ModelPreviewOptionRow
                {
                    Name = string.IsNullOrEmpty(group) ? option.Name : $"{group} / {option.Name}",
                    IsEnabled = source.Options.OptionToggles![i],
                    SubOptions = option.SubOptions?.Select(item => item.Name).ToArray() ?? [],
                    SelectedIndex = source.Options.OptionSelections![i]
                });
            }
        }
        else if (source.Manifest?.LegacyOptions is { } legacy)
        {
            foreach (var name in legacy) LegacyOptions.Add(name);
            LegacySelectedIndex = source.Options.LegacySelectedIndex ?? 0;
        }
        OnPropertyChanged(nameof(HasOptions)); OnPropertyChanged(nameof(HasLegacyOptions));
    }

    [RelayCommand]
    private Task ReloadAsync() => _source is null || !IsOpen ? Task.CompletedTask : RunAsync(LoadAsync);

    private async Task LoadAsync(CancellationToken token)
    {
        var source = _source!;
        var state = HasLegacyOptions ? new ProfileModOptionsState { LegacySelectedIndex = LegacySelectedIndex }
            : new ProfileModOptionsState { OptionToggles = Options.Select(option => option.IsEnabled).ToList(),
                OptionSelections = Options.Select(option => option.SelectedIndex).ToList() };
        var resolution = TextureResolution;
        ClearResult();
        var result = await _service.LoadAsync(source.ModId, state.ToJson(), token);
        token.ThrowIfCancellationRequested();
        var (selection, rotation) = await Task.Run(() => (ModelPreviewMeshSelector.Select(result.Meshes),
            ModelPreviewCharacterOrientation.GetRequiredRotation(result.Meshes)), token);
        var textureIds = selection.VisibleMeshes.Select(mesh => mesh.ColorTextureId).OfType<ulong>().Distinct().ToArray();
        var textures = await _service.LoadTexturesAsync(source.ModId, result.Textures, textureIds, resolution, token);
        var normalized = await Task.Run(() => NormalizeTextures(textures, resolution, token), token);
        token.ThrowIfCancellationRequested();
        _result = result; _textures = normalized;
        _missingTextureCount = textureIds.Length - normalized.Count;
        _presentationTransform = GetPresentationTransform(rotation);
        Error = result.Error ?? string.Empty;
        _updatingRows = true;
        try
        {
            foreach (var mesh in result.Meshes)
            {
                var row = new ModelPreviewMeshRow(mesh);
                row.PropertyChanged += OnMeshChanged;
                Meshes.Add(row);
            }
        }
        finally { _updatingRows = false; }
        OnPropertyChanged(nameof(HasBodyShapes));
        RebuildScene();
    }

    private static IReadOnlyDictionary<ulong, TexturePreviewData> NormalizeTextures(
        IReadOnlyDictionary<ulong, TexturePreviewData> source, int resolution, CancellationToken token)
    {
        var result = new Dictionary<ulong, TexturePreviewData>();
        foreach (var (id, texture) in source)
        {
            token.ThrowIfCancellationRequested();
            if (texture.BgraPixels is not null) { result.Add(id, texture); continue; }
            if (texture.EncodedImageBytes is not { } bytes) continue;
            try
            {
                using var stream = new MemoryStream(bytes);
                var targetWidth = resolution == ModelPreviewService.OriginalTextureResolution
                    ? texture.Width
                    : Math.Min(resolution, texture.Width);
                using var bitmap = Bitmap.DecodeToWidth(stream, targetWidth);
                using var converted = new WriteableBitmap(bitmap.PixelSize, bitmap.Dpi,
                    Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
                using var buffer = converted.Lock();
                bitmap.CopyPixels(buffer);
                var stride = checked(bitmap.PixelSize.Width * 4);
                var pixels = new byte[checked(stride * bitmap.PixelSize.Height)];
                for (var row = 0; row < bitmap.PixelSize.Height; row++)
                    Marshal.Copy(buffer.Address + row * buffer.RowBytes, pixels, row * stride, stride);
                result.Add(id, new TexturePreviewData { Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height,
                    BgraPixels = pixels, Description = texture.Description });
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceWarning("Texture {0:x16}: {1}", id, error.Message);
            }
        }
        return result;
    }

    private void OnMeshChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_updatingRows && args.PropertyName == nameof(ModelPreviewMeshRow.IsVisible)) RebuildScene();
    }

    private void RebuildScene()
    {
        if (_result is null || !IsOpen) { Scene = null; return; }
        var meshes = Meshes.Where(row => row.IsVisible &&
            (ShowFilteredMeshes || row.Mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible)).Select(row => row.Mesh).ToArray();
        var filtered = ModelPreviewBodyShapeSelection.Filter(meshes, meshes, ShowStockyBody);
        if (IsolateSelectedMesh && SelectedMesh is { } selected)
            filtered = filtered.Where(mesh => ReferenceEquals(mesh, selected.Mesh)).ToArray();
        Scene = new(filtered, _textures, _presentationTransform);
        OnPropertyChanged(nameof(Summary));
    }

    private static Matrix4x4 GetPresentationTransform(ModelPreviewPresentationRotation rotation) => rotation switch
    {
        ModelPreviewPresentationRotation.PositiveZToPositiveY => Matrix4x4.CreateRotationX(-MathF.PI / 2),
        ModelPreviewPresentationRotation.NegativeZToPositiveY => Matrix4x4.CreateRotationX(MathF.PI / 2),
        ModelPreviewPresentationRotation.PositiveXToPositiveY => Matrix4x4.CreateRotationZ(MathF.PI / 2),
        ModelPreviewPresentationRotation.NegativeXToPositiveY => Matrix4x4.CreateRotationZ(-MathF.PI / 2),
        _ => Matrix4x4.Identity
    };

    private Task RunAsync(Func<CancellationToken, Task> work)
    {
        _cancellation?.Cancel();
        var previous = _operation;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var generation = ++_generation;
        IsBusy = true; Error = string.Empty;
        _operation = ExecuteAsync();
        return _operation;

        async Task ExecuteAsync()
        {
            try
            {
                await previous;
                cancellation.Token.ThrowIfCancellationRequested();
                await work(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (generation == _generation) Error = error.Message;
            }
            finally
            {
                if (generation == _generation) { IsBusy = false; _cancellation = null; }
                cancellation.Dispose();
            }
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private Task CloseAsync() => StopAsync();

    public async Task StopAsync()
    {
        IsOpen = false;
        var generation = ++_generation;
        _cancellation?.Cancel(); _cancellation = null;
        ClearContent();
        await _operation;
        if (generation == _generation) IsBusy = false;
    }

    private void ClearContent()
    {
        _source = null; Title = string.Empty; Error = string.Empty;
        Options.Clear(); LegacyOptions.Clear();
        OnPropertyChanged(nameof(HasOptions)); OnPropertyChanged(nameof(HasLegacyOptions));
        ClearResult();
    }

    private void ClearResult()
    {
        _result = null; Scene = null; SelectedMesh = null; _missingTextureCount = 0;
        foreach (var row in Meshes) row.PropertyChanged -= OnMeshChanged;
        Meshes.Clear();
        _textures = new Dictionary<ulong, TexturePreviewData>();
        OnPropertyChanged(nameof(HasBodyShapes)); OnPropertyChanged(nameof(Summary));
    }

    public void ReportRenderFailure(string message) => Error = message;
}

public partial class ModelPreviewOptionRow : ObservableObject
{
    public required string Name { get; init; }
    public string[] SubOptions { get; init; } = [];
    public bool HasSubOptions => SubOptions.Length > 0;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private int _selectedIndex;
}

public partial class ModelPreviewMeshRow(ModelPreviewMesh mesh) : ObservableObject
{
    public ModelPreviewMesh Mesh { get; } = mesh;
    public string Name => $"{Mesh.UnitId:x16} / {Mesh.MeshInfoIndex} / {Mesh.StreamIndex}";
    public string Details => $"{Mesh.PatchFile} · {Mesh.VertexCount:N0} / {Mesh.TriangleCount:N0}";
    [ObservableProperty] private bool _isVisible = true;
}
