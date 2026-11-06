using System.ComponentModel;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>模型下拉项：addon 内相对路径 + 供人阅读的骨骼/网格/开关/动骨/材质概况。</summary>
public sealed class GmodModelOption : INotifyPropertyChanged
{
    private readonly GmodAddonModel? _model;
    private readonly UiLocalization _localization;

    private GmodModelOption(string path, GmodAddonModel? model, UiLocalization localization)
    {
        Path = path;
        _model = model;
        _localization = localization;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    /// <summary>无可解析的模型数据（手动输入路径）时不显示概况。</summary>
    public string Detail => _model is null ? "" : _localization.Format("GmodIas.ModelDetail",
        _model.BoneCount, _model.MeshCount, _model.BodyGroupCount, _model.JiggleBoneCount, _model.MaterialCount);

    public static GmodModelOption From(GmodAddonModel model, UiLocalization localization) =>
        new(model.Path, model, localization);

    /// <summary>手动选择、且不在分析结果内的模型（例如 addon 之外的本地文件）。</summary>
    public static GmodModelOption Manual(string path, UiLocalization localization) => new(path, null, localization);

    /// <summary>语言切换后刷新概况文案。</summary>
    public void RefreshLocalizedText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));

    public override string ToString() => Path;
}
