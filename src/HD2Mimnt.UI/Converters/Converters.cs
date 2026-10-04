using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace HD2Mimnt.UI.Converters;

/// <summary>
/// Bool 转颜色转换器
/// true → Green, false → Gray
/// </summary>
public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isTrue)
        {
            return isTrue
                ? new SolidColorBrush(Color.Parse("#10B981")) // Green
                : new SolidColorBrush(Color.Parse("#6B7280")); // Gray
        }
        return new SolidColorBrush(Color.Parse("#6B7280"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag && !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool flag && !flag;
}

/// <summary>
/// 大于零转换器
/// 用于显示/隐藏徽章
/// </summary>
public class GreaterThanZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count)
        {
            return count > 0;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

/// <summary>
/// 相等比较转换器
/// 用于 RadioButton 选中状态
/// </summary>
public class EqualConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value != null && parameter != null)
        {
            return value.ToString() == parameter.ToString();
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isTrue && isTrue && parameter != null)
        {
            // 尝试转换参数到目标类型
            if (int.TryParse(parameter.ToString(), out int intValue))
            {
                return intValue;
            }
            return parameter.ToString();
        }
        return null;
    }
}

/// <summary>
/// 非空转换器
/// 用于控制元素可见性
/// </summary>
public class NotNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value != null && !string.IsNullOrWhiteSpace(value.ToString());
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

/// <summary>
/// 时间转"几分钟前"格式
/// </summary>
public class TimeAgoConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dateTime)
        {
            var now = DateTime.UtcNow;
            var diff = now - dateTime;

            if (diff.TotalSeconds < 60)
                return "just now";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays}d ago";

            return dateTime.ToLocalTime().ToString("MMM dd");
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

/// <summary>
/// 通知级别转颜色
/// </summary>
public class LevelToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Application.Services.NotificationLevel level)
        {
            return level switch
            {
                Application.Services.NotificationLevel.Success => new SolidColorBrush(Color.Parse("#10B981")),
                Application.Services.NotificationLevel.Info => new SolidColorBrush(Color.Parse("#38BDF8")),
                Application.Services.NotificationLevel.Warning => new SolidColorBrush(Color.Parse("#F59E0B")),
                Application.Services.NotificationLevel.Error => new SolidColorBrush(Color.Parse("#EF4444")),
                _ => new SolidColorBrush(Color.Parse("#6B7280"))
            };
        }
        return new SolidColorBrush(Color.Parse("#6B7280"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

/// <summary>
/// 通知级别转图标
/// </summary>
public class LevelToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Application.Services.NotificationLevel level)
        {
            return level switch
            {
                Application.Services.NotificationLevel.Success => "✓",
                Application.Services.NotificationLevel.Info => "ℹ",
                Application.Services.NotificationLevel.Warning => "⚠",
                Application.Services.NotificationLevel.Error => "✕",
                _ => "•"
            };
        }
        return "•";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}
