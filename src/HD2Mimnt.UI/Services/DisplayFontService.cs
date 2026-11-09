using System;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Styling;

namespace HD2Mimnt.UI.Services;

/// <summary>显示字体按外置文件加载：字体留在应用目录的 Assets/Fonts 下，不内嵌进程序集，皮肤只声明回退字体。</summary>
public static class DisplayFontService
{
    public const string DisplayFontKey = "DisplayFontFamily";

    private const string FontFileName = "Monocraft.ttf";
    private const string FamilyName = "Monocraft";
    private const string FallbackFamilyNames = "Microsoft YaHei UI";
    private const string CollectionKey = "HD2MimntDisplay";

    private static readonly object Gate = new();

    /// <summary>字体集合注册在各自 Application 的 FontManager 上，按实例缓存才不会用到已失效的注册。</summary>
    private static readonly ConditionalWeakTable<FontManager, FontFamily?> Cache = new();

    /// <summary>把外置显示字体覆盖进声明了 DisplayFontFamily 的皮肤字典；字体文件缺失时保持皮肤自带的回退字体。</summary>
    public static void ApplyTo(ResourceDictionary skin)
    {
        if (!skin.TryGetResource(DisplayFontKey, ThemeVariant.Default, out _)) return;
        if (Resolve() is { } font) skin[DisplayFontKey] = font;
    }

    private static FontFamily? Resolve()
    {
        var manager = FontManager.Current;
        lock (Gate)
        {
            if (Cache.TryGetValue(manager, out var cached)) return cached;
            var font = Register(manager);
            Cache.Add(manager, font);
            return font;
        }
    }

    private static FontFamily? Register(FontManager manager)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
            if (!File.Exists(Path.Combine(directory, FontFileName))) return null;

            var source = new Uri(directory + Path.DirectorySeparatorChar);
            // 集合 Key 只认 fonts: 方案，且必须与 FontFamily 解析出的来源完全一致（含尾斜杠），否则静默回退系统字体。
            var key = new Uri($"{FontManager.FontCollectionScheme}:{CollectionKey}/");
            manager.AddFontCollection(new EmbeddedFontCollection(key, source));
            return new FontFamily($"{key}#{FamilyName}, {FallbackFamilyNames}");
        }
        catch
        {
            // 字体缺失或格式异常都不能阻断换肤，交给皮肤自带的回退字体。
            return null;
        }
    }
}
