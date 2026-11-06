using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 工具页「GMOD 模型转换」：选定 addon 后自动扫描其内容并给出可转换模型下拉，
/// 再收集输出目录与 IAS 发布参数，通过 <see cref="GmodIasConversionService"/> 执行转换并展示保真报告。
/// 文件读写、格式解析与资源生命周期均不放在 UI 层。
/// </summary>
public partial class GmodIasConversionViewModel : ObservableObject, IDisposable
{
    private const int AnalysisDebounceMilliseconds = 350;

    private readonly GmodIasConversionService _service;
    private readonly IConfirmationService _confirmation;
    private readonly UiLocalization _localization;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _analysisCancellation;
    private string _statusKey = "GmodIas.Ready";
    private object?[] _statusArguments = [];
    private GmodIasConversionOutcome? _outcome;
    private GmodAddonAnalysis? _analysis;

    [ObservableProperty] private string _addonPath = "";
    [ObservableProperty] private string _modelPath = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private string _projectId = "";
    [ObservableProperty] private string _archiveId = "";
    [ObservableProperty] private string _equipmentKey = "";
    [ObservableProperty] private string _nativeUnitIds = "";
    [ObservableProperty] private string _helmetArchiveId = "";
    [ObservableProperty] private string _switchSelection = "";
    [ObservableProperty] private int _domainIndex;
    [ObservableProperty] private int _switchModeIndex;
    [ObservableProperty] private int _partSplitIndex;
    [ObservableProperty] private bool _includeSkin = true;
    [ObservableProperty] private bool _includePhysics = true;
    [ObservableProperty] private bool _includeRawSource = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private GmodModelOption? _selectedModel;
    [ObservableProperty] private GmodArmorOption? _selectedBodyArmor;
    [ObservableProperty] private GmodArmorOption? _selectedHelmetArmor;

    public GmodIasConversionViewModel(GmodIasConversionService service, IConfirmationService confirmation,
        UiLocalization localization)
    {
        _service = service;
        _confirmation = confirmation;
        _localization = localization;
        _localization.PropertyChanged += OnLanguageChanged;
        LoadArmorCatalog();
    }

    /// <summary>addon 内可转换的模型，绑定到模型下拉框。</summary>
    public ObservableCollection<GmodModelOption> ModelOptions { get; } = [];

    /// <summary>可作为替换目标的身体护甲；第 0 项固定是「不指定」。</summary>
    public ObservableCollection<GmodArmorOption> BodyArmors { get; } = [];

    /// <summary>可作为替换目标的头盔；第 0 项固定是「不指定」。</summary>
    public ObservableCollection<GmodArmorOption> HelmetArmors { get; } = [];

    public bool HasArmorCatalog => BodyArmors.Count > 1 || HelmetArmors.Count > 1;

    /// <summary>目标护甲目录的加载结果（文件名或错误信息），用于界面提示。</summary>
    public string ArmorCatalogNote { get; private set; } = "";

    public bool HasArmorCatalogNote => ArmorCatalogNote.Length > 0;

    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>当前（或最近一次）addon 扫描任务，供测试与调用方等待扫描结束。</summary>
    public Task AnalysisTask { get; private set; } = Task.CompletedTask;
    public bool CanEdit => !IsBusy;
    public bool CanCancel => IsBusy && _cancellation?.IsCancellationRequested == false;
    public bool CanAnalyze => !IsBusy && !IsAnalyzing && !string.IsNullOrWhiteSpace(AddonPath);
    public bool HasModelOptions => ModelOptions.Count > 0;
    public bool CanStart => !IsBusy && HasRequiredPaths;
    private bool HasRequiredPaths => !string.IsNullOrWhiteSpace(AddonPath) &&
        !string.IsNullOrWhiteSpace(ModelPath) && !string.IsNullOrWhiteSpace(OutputDirectory);

    public string StatusMessage => _localization.Format(_statusKey, _statusArguments);

    /// <summary>分析阶段的补充说明（未找到可转换模型、无法解析的 .mdl 等）。</summary>
    public string AnalysisNote => _analysis is null || _analysis.Issues.Count == 0
        ? ""
        : string.Join("\n", _analysis.Issues.Select(issue => $"[{issue.Severity}] {issue.Code}: {issue.Asset} — {issue.Message}"));

    public bool HasAnalysisNote => AnalysisNote.Length > 0;

    public string ResultText
    {
        get
        {
            if (_outcome is null) return "";
            var report = _outcome.Report;
            var lines = new List<string>
            {
                _localization.Format("GmodIas.ResultOutput", _outcome.OutputDirectory, _outcome.FileCount)
            };
            foreach (var part in report.Parts)
            {
                var name = string.IsNullOrEmpty(part.Part) ? _localization["GmodIas.PartWhole"] : part.Part;
                lines.Add(_localization.Format("GmodIas.ResultPart", name, part.Domain, part.MeshCount,
                    part.TriangleCount, part.OptionCount));
            }
            lines.Add(_localization.Format("GmodIas.ResultGeometry", report.BoneCount, report.MeshCount,
                report.VertexCount, report.TriangleCount));
            lines.Add(_localization.Format("GmodIas.ResultAssets", report.MaterialCount, report.TextureCount,
                report.DecodedTextureCount, report.RawSourceFileCount));
            lines.Add(_localization.Format("GmodIas.ResultSwitches", report.SwitchCount, report.OptionCount,
                report.RigProfileCount));
            lines.Add(_localization.Format("GmodIas.ResultPhysics", report.PhysicsChainCount,
                report.PhysicsJointCount, report.PhysicsProfileCount));
            lines.Add(_localization.Format("GmodIas.ResultPackage",
                report.Parts.Sum(part => part.UnitCount), report.Parts.Sum(part => part.MaterialResourceCount),
                report.Parts.Sum(part => part.TextureResourceCount),
                FormatSize(report.Parts.Sum(part => part.PackageBytes))));
            lines.Add(_localization.Format("GmodIas.ResultRuntimePack", report.RuntimePack,
                FormatSize(report.RuntimePackBytes)));
            lines.Add(_localization.Format("GmodIas.ResultTarget", string.Join(" / ",
                report.Parts.Select(part => $"{PartName(part)}: {(part.TargetArmor.Length == 0
                    ? _localization["GmodIas.ArmorNone"]
                    : part.TargetArmor)}" +
                    (part.CleanupUnitCount > 0 ? $" (+{part.CleanupUnitCount})" : "")))));
            return string.Join("\n", lines);
        }
    }

    private string PartName(GmodIasPartSummary part) =>
        string.IsNullOrEmpty(part.Part) ? _localization["GmodIas.PartWhole"] : part.Part;

    /// <summary>补丁体积的人类可读表示（界面只显示总量，详细拆分在 runnable.json）。</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KiB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MiB"
    };

    /// <summary>转换器产出的技术诊断保持原文，不参与翻译反查。</summary>
    public string IssuesText => _outcome is null ? "" : string.Join("\n",
        _outcome.Report.Issues.Select(issue => $"[{issue.Severity}] {issue.Code}: {issue.Message}"));

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAddonAsync() => RunAnalysisAsync(AddonPath.Trim(), CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task ConvertAsync() => OperateAsync(async token =>
    {
        var project = string.IsNullOrWhiteSpace(ProjectId)
            ? GmodIasConversionService.DeriveProjectId(ModelPath) : ProjectId.Trim();
        ulong archive;
        if (string.IsNullOrWhiteSpace(ArchiveId))
            archive = GmodIasConversionService.DeriveArchiveId(project, ModelPath);
        else if (!GmodIasConversionService.TryParseHexId(ArchiveId, out archive) || archive == 0)
        {
            SetStatus("GmodIas.InvalidArchive"); return;
        }
        ulong equipment = 1;
        if (!string.IsNullOrWhiteSpace(EquipmentKey) &&
            (!GmodIasConversionService.TryParseHexId(EquipmentKey, out equipment) ||
             equipment is 0 or > uint.MaxValue))
        {
            SetStatus("GmodIas.InvalidEquipment"); return;
        }
        IReadOnlyList<ulong> native = [archive];
        if (!string.IsNullOrWhiteSpace(NativeUnitIds) &&
            !GmodIasConversionService.TryParseNativeIds(NativeUnitIds, out native))
        {
            SetStatus("GmodIas.InvalidNative"); return;
        }
        var domain = DomainIndex == 1 ? GmodIasDomain.Helmet : GmodIasDomain.Body;
        var mode = SwitchModeIndex switch
        {
            1 => GmodIasSwitchMode.Independent,
            2 => GmodIasSwitchMode.Composite,
            _ => GmodIasSwitchMode.Auto
        };
        var split = PartSplitIndex switch
        {
            1 => GmodIasPartSplit.Body,
            2 => GmodIasPartSplit.Helmet,
            3 => GmodIasPartSplit.None,
            _ => GmodIasPartSplit.Both
        };
        ulong? helmetArchive = null;
        if (!string.IsNullOrWhiteSpace(HelmetArchiveId))
        {
            if (!GmodIasConversionService.TryParseHexId(HelmetArchiveId, out var value) || value == 0)
            {
                SetStatus("GmodIas.InvalidArchive"); return;
            }
            helmetArchive = value;
        }
        // 空串表示发布全部开关；写了不合法的键就提示，而不是静默少发开关。
        IReadOnlyList<string>? exposedSwitches = null;
        if (!string.IsNullOrWhiteSpace(SwitchSelection))
        {
            if (!GmodIasConversionService.TryParseSwitchKeys(SwitchSelection, out var switches))
            {
                SetStatus("GmodIas.InvalidSwitchSelection"); return;
            }
            exposedSwitches = switches;
        }

        // 输出目录可能覆盖同名文件，写入前显式确认；取消不产生任何文件。
        var confirmed = await _confirmation.ConfirmAsync(_localization["GmodIas.ConfirmTitle"],
            _localization.Format("GmodIas.ConfirmMessage", OutputDirectory.Trim())).WaitAsync(token);
        if (!confirmed) { SetStatus("GmodIas.Cancelled"); return; }
        token.ThrowIfCancellationRequested();

        SetStatus("GmodIas.Converting");
        var outcome = await _service.ConvertAsync(new GmodIasConversionRequest(
            AddonPath.Trim(), ModelPath.Trim(), OutputDirectory.Trim(), project, archive, domain,
            (uint)equipment, native, mode, IncludeSkin, IncludePhysics, IncludeRawSource, split,
            helmetArchive, exposedSwitches, BodyArmor: SelectedBodyArmor?.Target,
            HelmetArmor: SelectedHelmetArmor?.Target), token);
        _outcome = outcome;
        HasResult = true;
        SetStatus("GmodIas.Completed", outcome.FileCount);
    });

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _cancellation?.Cancel(); CancelCommand.NotifyCanExecuteChanged(); }

    public async Task CancelAndWaitAsync() { _cancellation?.Cancel(); await Completion; }

    /// <summary>文件/目录选择器（View 代码后置）回填；赋值本身会触发一次内容扫描。</summary>
    public void AcceptAddonPath(string path) => AddonPath = path;

    public void AcceptModelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var match = ModelOptions.FirstOrDefault(option =>
            string.Equals(option.Path, path, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            match = GmodModelOption.Manual(path, _localization);
            ModelOptions.Add(match);
            OnPropertyChanged(nameof(HasModelOptions));
        }
        SelectedModel = match;
        ModelPath = match.Path;
    }

    public void AcceptOutputDirectory(string path) => OutputDirectory = path;

    /// <summary>
    /// 加载随程序分发的目标护甲目录。目录缺失时下拉框只剩「不指定」，
    /// 用户仍可用高级面板手填 Archive / Native Unit（那种产物在游戏里不会挂到护甲上）。
    /// </summary>
    private void LoadArmorCatalog()
    {
        // 语言切换会重建下拉项，这里按 Archive 记住用户原本的选择。
        var bodyArchive = SelectedBodyArmor?.Target?.ArchiveId;
        var helmetArchive = SelectedHelmetArmor?.Target?.ArchiveId;
        GmodArmorTargetService? catalog = null;
        try
        {
            catalog = GmodArmorTargetService.TryLoadDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ArmorCatalogNote = _localization.Format("GmodIas.ArmorCatalogInvalid", error.Message);
        }

        if (catalog is null)
        {
            if (ArmorCatalogNote.Length == 0)
                ArmorCatalogNote = _localization.Format("GmodIas.ArmorCatalogMissing", GmodArmorTargetService.DefaultPath);
            FillArmorOptions(BodyArmors, []);
            FillArmorOptions(HelmetArmors, []);
        }
        else
        {
            var bodies = catalog.Bodies.ToArray();
            var helmets = catalog.Helmets.ToArray();
            FillArmorOptions(BodyArmors, bodies);
            FillArmorOptions(HelmetArmors, helmets);
            if (ArmorCatalogNote.Length == 0)
                ArmorCatalogNote = _localization.Format("GmodIas.ArmorCatalogLoaded", bodies.Length, helmets.Length);
        }

        SelectedBodyArmor = bodyArchive is null
            ? BodyArmors[0]
            : BodyArmors.FirstOrDefault(option => option.Target?.ArchiveId == bodyArchive) ?? BodyArmors[0];
        SelectedHelmetArmor = helmetArchive is null
            ? HelmetArmors[0]
            : HelmetArmors.FirstOrDefault(option => option.Target?.ArchiveId == helmetArchive) ?? HelmetArmors[0];
        OnPropertyChanged(nameof(HasArmorCatalog));
        OnPropertyChanged(nameof(ArmorCatalogNote));
        OnPropertyChanged(nameof(HasArmorCatalogNote));
    }

    private void FillArmorOptions(ObservableCollection<GmodArmorOption> target, IEnumerable<GmodIasArmorTarget> armors)
    {
        target.Clear();
        target.Add(new GmodArmorOption(_localization["GmodIas.ArmorNone"], null));
        foreach (var armor in armors) target.Add(new GmodArmorOption(armor.DisplayName, armor));
    }

    /// <summary>切换护甲时把 Archive / equipment 回填到高级面板，让手填值与实际绑定保持一致。</summary>
    partial void OnSelectedBodyArmorChanged(GmodArmorOption? value) => ApplyArmorToAdvancedPanel(value, false);

    partial void OnSelectedHelmetArmorChanged(GmodArmorOption? value) => ApplyArmorToAdvancedPanel(value, true);

    private void ApplyArmorToAdvancedPanel(GmodArmorOption? option, bool helmet)
    {
        if (option?.Target is not { } target) return;
        if (helmet) HelmetArchiveId = target.ArchiveHex;
        ArchiveId = target.ArchiveHex;
        EquipmentKey = "0x" + target.EquipmentKeys[0].ToString("X8");
        NativeUnitIds = string.Join(",", target.UnitIds.Select(id => "0x" + id.ToString("x16")));
    }

    /// <summary>模型必须落在 addon 内；View 在无法推导相对路径时把原因写回状态栏。</summary>
    public void ReportAddonRequired() => SetStatus("GmodIas.AddonRequired");
    public void ReportModelOutsideAddon() => SetStatus("GmodIas.ModelOutsideAddon");
    public void ReportGmaManualPath() => SetStatus("GmodIas.GmaManualPath");

    partial void OnAddonPathChanged(string value)
    {
        RefreshCommands();
        QueueAnalysis();
    }

    partial void OnModelPathChanged(string value) => RefreshCommands();
    partial void OnOutputDirectoryChanged(string value) => RefreshCommands();
    partial void OnSelectedModelChanged(GmodModelOption? value)
    {
        if (value is not null && !string.Equals(ModelPath, value.Path, StringComparison.Ordinal))
            ModelPath = value.Path;
        RefreshCommands();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(StatusMessage));
        RefreshCommands();
    }

    partial void OnIsAnalyzingChanged(bool value) => RefreshCommands();
    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(ResultText));

    /// <summary>addon 路径变化时防抖扫描，避免逐字符输入时反复解包 GMA。</summary>
    private void QueueAnalysis()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = null;
        if (string.IsNullOrWhiteSpace(AddonPath))
        {
            ResetAnalysis();
            SetStatus("GmodIas.Ready");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        AnalysisTask = DebounceAsync(AddonPath.Trim(), cancellation);
    }

    private async Task DebounceAsync(string addonPath, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(AnalysisDebounceMilliseconds, cancellation.Token);
            await RunAnalysisAsync(addonPath, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户继续输入或切换了 addon，由新的分析任务接管。
        }
        finally
        {
            if (ReferenceEquals(_analysisCancellation, cancellation)) _analysisCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task RunAnalysisAsync(string addonPath, CancellationToken cancellationToken)
    {
        if (IsBusy || IsAnalyzing || string.IsNullOrWhiteSpace(addonPath)) return;
        IsAnalyzing = true;
        SetStatus("GmodIas.Analyzing");
        try
        {
            var analysis = await _service.AnalyzeAsync(addonPath, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            ApplyAnalysis(analysis);
        }
        catch (OperationCanceledException)
        {
            // 被新的分析请求取代。
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // 用户还在逐字输入路径时不应报错，等路径生效后再自动扫描。
            ResetAnalysis();
            SetStatus("GmodIas.Ready");
        }
        catch (Exception error)
        {
            ResetAnalysis();
            SetStatus("GmodIas.AnalysisFailed", error.Message);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private void ApplyAnalysis(GmodAddonAnalysis analysis)
    {
        var previous = SelectedModel?.Path ?? ModelPath;
        _analysis = analysis;
        ModelOptions.Clear();
        foreach (var model in analysis.Models.Where(model => model.IsConvertible))
            ModelOptions.Add(GmodModelOption.From(model, _localization));
        OnPropertyChanged(nameof(HasModelOptions));

        var match = ModelOptions.FirstOrDefault(option =>
            string.Equals(option.Path, previous, StringComparison.OrdinalIgnoreCase));
        SelectedModel = match ?? ModelOptions.FirstOrDefault();
        if (SelectedModel is null) ModelPath = "";

        var skipped = analysis.Models.Count - ModelOptions.Count;
        if (ModelOptions.Count == 0)
            SetStatus("GmodIas.AnalyzedEmpty", analysis.Models.Count);
        else if (skipped > 0)
            SetStatus("GmodIas.AnalyzedWithSkipped", ModelOptions.Count, skipped);
        else
            SetStatus("GmodIas.Analyzed", ModelOptions.Count);
        OnPropertyChanged(nameof(AnalysisNote));
        OnPropertyChanged(nameof(HasAnalysisNote));
    }

    private void ResetAnalysis()
    {
        _analysis = null;
        OnPropertyChanged(nameof(AnalysisNote));
        OnPropertyChanged(nameof(HasAnalysisNote));
        if (ModelOptions.Count == 0) return;
        ModelOptions.Clear();
        SelectedModel = null;
        ModelPath = "";
        OnPropertyChanged(nameof(HasModelOptions));
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanAnalyze));
        ConvertCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        AnalyzeAddonCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        OnPropertyChanged(nameof(StatusMessage));
    }

    private async Task OperateAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = completion.Task;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _outcome = null;
        HasResult = false;
        SetStatus("GmodIas.Converting");
        IsBusy = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { SetStatus("GmodIas.Cancelled"); }
        catch (Exception error) { SetStatus("GmodIas.Failed", error.Message); }
        finally
        {
            _cancellation = null;
            IsBusy = false;
            completion.TrySetResult();
        }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(UiLocalization.Revision)) return;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ResultText));
        foreach (var option in ModelOptions) option.RefreshLocalizedText();
        ArmorCatalogNote = "";
        LoadArmorCatalog();
    }

    public void Dispose()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _cancellation?.Cancel();
        _localization.PropertyChanged -= OnLanguageChanged;
    }
}
