using HD2Mimnt.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Core.Entities;
using Avalonia.Media.Imaging;
using Avalonia.Media;

namespace HD2Mimnt.UI.ViewModels;

public abstract partial class ProfileModImageHolderViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private Bitmap? _image;

    internal string? ImagePath { get; set; }

    /// <summary>
    /// 卡片实际显示尺寸只有 40～64 像素；按原图尺寸解码时，一张 4K 预览图就要占约 32 MiB 原生内存，
    /// 打开带预览图的 HD2IAS 选项弹窗会瞬间抬升数百 MiB。统一按 128 像素宽解码（2x 屏足够清晰）。
    /// </summary>
    internal const int CardImageDecodeWidth = 128;

    private bool _disposed;

    public bool IsDisposed => _disposed;

    public bool HasImage => Image != null;

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));

    public async Task LoadImageAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Image != null || string.IsNullOrEmpty(ImagePath)) return;
        Bitmap? bitmap;
        try
        {
            // 同一路径或内容哈希相同的副本复用同一张解码位图，避免反复解码同一张图。
            bitmap = await ModImageCache.DecodeSharedAsync(ImagePath, CardImageDecodeWidth, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // 预览图缺失或损坏时保持无图占位
            return;
        }
        // 解码期间弹窗可能已关闭；共享位图由弹窗统一释放，这里只丢弃引用。
        if (_disposed || cancellationToken.IsCancellationRequested) return;
        Image = bitmap;
    }

    /// <summary>按需解码原图供覆盖层预览；调用方负责在预览关闭后释放。</summary>
    public async Task<Bitmap?> LoadOriginalImageAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrEmpty(ImagePath)) return null;
        var path = ImagePath;
        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 原图不可读时由调用方回退到已加载的小图。
            return null;
        }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 相同内容的卡片共享同一张位图，不能各自 Dispose；由弹窗按实例去重后统一释放。
        Image = null;
    }
}

/// <summary>v1 选项卡片：独立开关 + 可选的子选项（多选一）。</summary>
public partial class ProfileModOptionCardViewModel : ProfileModImageHolderViewModel
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public IReadOnlyList<ProfileModSubOptionChoiceViewModel> SubOptions { get; init; } =
        Array.Empty<ProfileModSubOptionChoiceViewModel>();

    public bool HasSubOptions => SubOptions.Count > 0;

    /// <summary>所属组件分组（HD2IAS 导入生成）；null 表示单级选项。</summary>
    public string? GroupId { get; init; }

    public override void Dispose()
    {
        if (IsDisposed) return;
        foreach (var sub in SubOptions) sub.Dispose();
        base.Dispose();
    }
}

/// <summary>一级组件卡片（HD2IAS 角色包）：进入二级页配置该组件的选项与子选项。</summary>
public partial class ProfileModOptionGroupViewModel : ProfileModImageHolderViewModel
{
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public IReadOnlyList<ProfileModOptionCardViewModel> Options { get; init; } =
        Array.Empty<ProfileModOptionCardViewModel>();

    public bool HasSubOptions => Options.Any(option => option.SubOptions.Count > 0);

    /// <summary>只有一个无变体选项时直接在一级开关，无需进入二级页。</summary>
    public bool CanOpen => Options.Count > 1 || HasSubOptions;
}

/// <summary>v1 选项内的子选项（多选一变体），带预览图。</summary>
public partial class ProfileModSubOptionChoiceViewModel : ProfileModImageHolderViewModel
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

/// <summary>v0 模组的互斥选项（单选列表项）。</summary>
