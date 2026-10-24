using System.Globalization;
using System.Runtime.InteropServices;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Localization;

/// <summary>读取当前用户的 Windows 显示语言，独立于应用选择及区域格式设置。</summary>
public sealed class SystemLanguageProvider : ISystemLanguageProvider
{
    public CultureInfo GetCurrentCulture()
    {
        if (OperatingSystem.IsWindows())
        {
            var languageId = GetUserDefaultUILanguage();
            if (languageId != 0)
                return CultureInfo.GetCultureInfo(languageId);
        }

        return CultureInfo.CurrentUICulture;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();
}
