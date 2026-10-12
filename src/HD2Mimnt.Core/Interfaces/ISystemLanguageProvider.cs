using System.Globalization;

namespace HD2Mimnt.Core.Interfaces;

public interface ISystemLanguageProvider
{
    CultureInfo GetCurrentCulture();
}
