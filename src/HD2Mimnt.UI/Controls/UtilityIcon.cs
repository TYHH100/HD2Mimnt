using Avalonia;
using FluentAvalonia.UI.Controls;

namespace HD2Mimnt.UI.Controls;

/// <summary>保留页面的语义图标名称，统一使用 FluentAvalonia 内置的 Fluent 图标。</summary>
public sealed class UtilityIcon : FASymbolIcon
{
    public static readonly StyledProperty<string> KindProperty =
        AvaloniaProperty.Register<UtilityIcon, string>(nameof(Kind), "Package");

    private static readonly IReadOnlyDictionary<string, FASymbol> Symbols = new Dictionary<string, FASymbol>
    {
        ["Search"] = FASymbol.Find, ["Add"] = FASymbol.Add, ["Close"] = FASymbol.Cancel,
        // Fluent System Icons 中的专用字形，当前 FluentAvalonia 版本未提供命名枚举。
        ["BackspaceQWERTY"] = (FASymbol)0xE750, ["MultiSelect"] = (FASymbol)0xE762,
        ["More"] = FASymbol.More, ["Package"] = FASymbol.Library, ["Bell"] = FASymbol.Alert,
        ["Tasks"] = FASymbol.List, ["Chevron"] = FASymbol.ChevronRight, ["Back"] = FASymbol.Back,
        ["Folder"] = FASymbol.Folder, ["Settings"] = FASymbol.Settings, ["Sliders"] = FASymbol.Filter,
        ["Plugin"] = FASymbol.AllApps, ["Logs"] = FASymbol.Document, ["Database"] = FASymbol.Save,
        ["Code"] = FASymbol.Code, ["Info"] = FASymbol.Help, ["Refresh"] = FASymbol.Refresh,
        ["Deploy"] = FASymbol.Upload, ["Clean"] = FASymbol.Delete, ["Check"] = FASymbol.Checkmark,
        ["Copy"] = FASymbol.Copy, ["Edit"] = FASymbol.Edit, ["Home"] = FASymbol.Home,
        ["ListOrdered"] = FASymbol.List, ["Warning"] = FASymbol.Important, ["Wrench"] = FASymbol.Repair,
        ["Globe"] = FASymbol.Globe, ["Lock"] = FASymbol.Admin, ["Play"] = FASymbol.Play
    };

    public UtilityIcon()
    {
        Width = Height = FontSize = 16;
        Symbol = FASymbol.Library;
    }

    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty)
            Symbol = Symbols.GetValueOrDefault(Kind, FASymbol.Library);
        else if (change.Property == WidthProperty || change.Property == HeightProperty)
            FontSize = double.IsFinite(Width) && double.IsFinite(Height) ? Math.Min(Width, Height) : 16;
    }
}
