using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace HD2Mimnt.UI.Services;

/// <summary>切换界面主题；皮肤主题以深色变体为基础，叠加专属资源与控件主题。</summary>
public static class AppThemeService
{
    public const string Dark = "Dark";
    public const string DefaultTheme = Dark;
    public const string Light = "Light";
    public const string System = "System";
    public const string Ore = "Ore";
    public const string Helldivers = "Helldivers";

    private static readonly Uri[] HelldiversDictionaryUris =
    {
        new("avares://HD2Mimnt.UI/Styles/Skins/HelldiversSkin.axaml"),
        new("avares://HD2Mimnt.UI/Styles/Skins/HelldiversControls.axaml")
    };
    private static readonly Uri HelldiversStylesUri = new("avares://HD2Mimnt.UI/Styles/Skins/HelldiversStyles.axaml");

    private static readonly Uri[] OreDictionaryUris =
    {
        new("avares://HD2Mimnt.UI/Styles/Skins/OreSkin.axaml"),
        new("avares://HD2Mimnt.UI/Styles/Skins/OreControls.axaml")
    };
    private static readonly Uri OreStylesUri = new("avares://HD2Mimnt.UI/Styles/Skins/OreStyles.axaml");

    private static readonly IReadOnlyDictionary<string, (Uri[] Dictionaries, Uri Styles)> Skins =
        new Dictionary<string, (Uri[], Uri)>(StringComparer.OrdinalIgnoreCase)
        {
            [Ore] = (OreDictionaryUris, OreStylesUri),
            [Helldivers] = (HelldiversDictionaryUris, HelldiversStylesUri)
        };

    private static readonly ConditionalWeakTable<global::Avalonia.Application, AppliedSkin> ActiveSkins = new();

    public static void Apply(string theme)
    {
        var app = global::Avalonia.Application.Current;
        if (app == null) return;

        if (ActiveSkins.TryGetValue(app, out var activeSkin))
        {
            // 启动期设置加载与显式应用会对同一主题各调用一次；已生效时跳过，
            // 避免已显示的窗口被卸载重装皮肤产生闪动。
            if (string.Equals(activeSkin.Theme, theme, StringComparison.OrdinalIgnoreCase)) return;

            if (activeSkin.Styles != null) app.Styles.Remove(activeSkin.Styles);
            foreach (var dictionary in activeSkin.Dictionaries)
                app.Resources.MergedDictionaries.Remove(dictionary);
            ActiveSkins.Remove(app);
        }

        app.RequestedThemeVariant = theme switch
        {
            Light => ThemeVariant.Light,
            System => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };

        var applied = new AppliedSkin { Theme = theme };
        if (Skins.TryGetValue(theme, out var skin))
        {
            foreach (var uri in skin.Dictionaries)
            {
                if (AvaloniaXamlLoader.Load(uri) is not ResourceDictionary dictionary) continue;
                DisplayFontService.ApplyTo(dictionary);
                app.Resources.MergedDictionaries.Add(dictionary);
                applied.Dictionaries.Add(dictionary);
            }

            if (AvaloniaXamlLoader.Load(skin.Styles) is Styles styles)
            {
                app.Styles.Add(styles);
                applied.Styles = styles;
            }
        }

        ActiveSkins.Add(app, applied);
    }

    private sealed class AppliedSkin
    {
        public string Theme { get; init; } = string.Empty;
        public List<ResourceDictionary> Dictionaries { get; } = new();
        public Styles? Styles { get; set; }
    }
}
