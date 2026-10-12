using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace HD2Mimnt.UI.Localization;

public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => new MultiBinding
    {
        Mode = BindingMode.OneWay,
        Converter = new ResourceTextConverter(Key),
        Bindings = { new Binding(nameof(UiLocalization.Revision)) { Source = UiLocalization.Current } }
    };

    private sealed class ResourceTextConverter(string key) : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
            => UiLocalization.Current[key];
    }
}

/// <summary>明确标记的展示绑定，语言版本变化时重新翻译，双向编辑绑定不使用此扩展。</summary>
public sealed class LocalizedBindingExtension : MarkupExtension
{
    public LocalizedBindingExtension() { }
    public LocalizedBindingExtension(string path) => Path = path;
    public string? Path { get; set; }
    public string? FormatKey { get; set; }
    public IValueConverter? InnerConverter { get; set; }
    public object? TargetNullValue { get; set; }
    public bool TranslateParts { get; set; }
    public bool TranslateValue { get; set; }
    public string? LocalizeWhenPath { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding
        {
            Mode = BindingMode.OneWay,
            Converter = new LocalizedTextConverter(UiLocalization.Current, FormatKey, TranslateParts, TranslateValue, InnerConverter),
            Bindings =
            {
                new ReflectionBindingExtension(Path ?? string.Empty)
                    { TargetNullValue = TargetNullValue }.ProvideValue(serviceProvider),
                new Binding(nameof(UiLocalization.Revision)) { Source = UiLocalization.Current }
            }
        };
        if (LocalizeWhenPath != null)
            binding.Bindings.Add(new ReflectionBindingExtension(LocalizeWhenPath).ProvideValue(serviceProvider));
        return binding;
    }
}

public sealed class LocalizedTextConverter(UiLocalization localization, string? formatKey = null,
    bool translateParts = false, bool translateValue = false, IValueConverter? innerConverter = null) : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var value = values.Count > 0 ? values[0] : null;
        // 依赖语言的日期等转换器也必须随 Revision 重算，不能缓存旧语言结果。
        if (innerConverter != null) value = innerConverter.Convert(value, targetType, parameter, localization.Culture);
        if (values.Count > 2 && values[2] is not true) return value?.ToString() ?? string.Empty;
        if (translateValue) value = localization.Translate(value);
        if (formatKey != null) return localization.Format(formatKey, value);
        if (translateParts) return localization.TranslateResourceNames(value);
        return localization.Translate(value);
    }
}
