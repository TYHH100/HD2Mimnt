using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>合集模组转换前统一解析目标版本：单个版本直接返回，多个版本交给浮窗选择。</summary>
public static class Hd2ClVariantSelection
{
    public static async Task<(Hd2ClVariant? Variant, bool Canceled)> ResolveAsync(
        IHd2ClConversionService service, IHd2ClVariantPrompt? prompt, Guid modId)
    {
        var variants = await service.GetVariantsAsync(modId);
        if (variants.Count <= 1) return (null, false);
        if (prompt is null) throw new InvalidOperationException("该模组包含多个版本，但当前界面无法选择转换目标。");
        var chosen = await prompt.ChooseAsync(variants);
        return chosen is null ? (null, true) : (chosen, false);
    }
}
