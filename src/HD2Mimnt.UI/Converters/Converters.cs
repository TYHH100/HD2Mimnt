using Avalonia.Data;
using Avalonia.Data.Converters;
using System;
using System.Globalization;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.Converters;

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

/// <summary>已结束的任务才显示删除按钮；运行中和等待中的任务只能取消。</summary>
public class FinishedTaskConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is HD2Mimnt.Core.Entities.TaskStatus status
           && status != HD2Mimnt.Core.Entities.TaskStatus.Running
           && status != HD2Mimnt.Core.Entities.TaskStatus.Waiting;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
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
                return UiLocalization.Current["Time.JustNow"];
            if (diff.TotalMinutes < 60)
                return UiLocalization.Current.Format("Time.MinutesAgo", (int)diff.TotalMinutes);
            if (diff.TotalHours < 24)
                return UiLocalization.Current.Format("Time.HoursAgo", (int)diff.TotalHours);
            if (diff.TotalDays < 7)
                return UiLocalization.Current.Format("Time.DaysAgo", (int)diff.TotalDays);

            return dateTime.ToLocalTime().ToString("MMM dd", UiLocalization.Current.Culture);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return BindingOperations.DoNothing;
    }
}

/// <summary>通知级别映射到 FluentAvalonia 的语义状态，颜色与图标由主题控制。</summary>
public class LevelToSeverityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Application.Services.NotificationLevel.Success => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Success,
        Application.Services.NotificationLevel.Warning => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Warning,
        Application.Services.NotificationLevel.Error => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error,
        _ => FluentAvalonia.UI.Controls.FAInfoBarSeverity.Informational
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}

public class NotificationLevelLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Application.Services.NotificationLevel.Success => "成功",
        Application.Services.NotificationLevel.Warning => "提醒",
        Application.Services.NotificationLevel.Error => "错误",
        _ => "通知"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
