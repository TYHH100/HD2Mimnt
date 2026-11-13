using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace HD2Mimnt.UI.Services;

/// <summary>界面字体：内置默认、系统字体族或用户指定的字体文件；未选择时保持默认，不覆盖皮肤字体。</summary>
public static class AppFontService
{
    public const string DefaultFontFamily = "Segoe UI Variable, Segoe UI, Microsoft YaHei UI";
    public const string FontResourceKey = "UiFontFamily";
    public const string SourceDefault = "FontDefault";
    public const string SourceInstalled = "FontInstalled";
    public const string SourceFile = "FontFile";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, FontFamily?> FileFonts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把当前选择应用到应用级资源；默认字体通过移除覆盖恢复皮肤/内置字体。</summary>
    public static void Apply(string? source, string? familyName, string? filePath)
    {
        if (global::Avalonia.Application.Current is not { } app) return;
        var font = Resolve(source, familyName, filePath);
        if (font != null)
        {
            app.Resources[FontResourceKey] = font;
            // 皮肤用 DisplayFontFamily 绘制标题和控件文字，用户显式选择的字体要同时覆盖它才能全局生效。
            app.Resources[DisplayFontService.DisplayFontKey] = font;
            return;
        }
        app.Resources.Remove(FontResourceKey);
        app.Resources.Remove(DisplayFontService.DisplayFontKey);
    }

    public static FontFamily? Resolve(string? source, string? familyName, string? filePath) => source switch
    {
        SourceInstalled => string.IsNullOrWhiteSpace(familyName) ? null : new FontFamily($"{familyName}, {DefaultFontFamily}"),
        SourceFile => ResolveFileFont(filePath),
        _ => null
    };

    public static IReadOnlyList<string> GetInstalledFontFamilies()
    {
        try
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            return fonts.Families.Select(family => family.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCulture)
                .ToArray();
        }
        catch
        {
            // 无 GDI+ 的环境无法枚举系统字体；保留手动输入字体族名和字体文件两种方式。
            return Array.Empty<string>();
        }
    }

    private static FontFamily? ResolveFileFont(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(filePath); }
        catch { return null; }
        if (!File.Exists(fullPath)) return null;

        lock (Gate)
        {
            if (FileFonts.TryGetValue(fullPath, out var cached)) return cached;
            var font = RegisterFileFont(fullPath);
            FileFonts[fullPath] = font;
            return font;
        }
    }

    private static FontFamily? RegisterFileFont(string fullPath)
    {
        try
        {
            var familyName = FontFileReader.TryReadFamilyName(fullPath);
            if (string.IsNullOrWhiteSpace(familyName)) return null;

            // 集合 Key 只认 fonts: 方案，且必须与 FontFamily 的来源完全一致（含尾斜杠）。
            var key = $"{FontManager.FontCollectionScheme}:HD2MimntFont{Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(fullPath)):X}/";
            FontManager.Current.AddFontCollection(new EmbeddedFontCollection(new Uri(key), new Uri(fullPath)));
            return new FontFamily($"{key}#{familyName}, {DefaultFontFamily}");
        }
        catch
        {
            return null;
        }
    }
}
