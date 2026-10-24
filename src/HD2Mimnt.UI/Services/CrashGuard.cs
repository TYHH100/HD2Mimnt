using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using HD2Mimnt.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 全局异常兜底：UI 线程异常、AppDomain 未处理异常与未观察 Task 异常统一记录并通知，
/// 避免未捕获异常直接崩溃或静默丢失。在 Program.Main 最早处 <see cref="Install"/>；
/// DI 就绪后 <see cref="AttachLogging"/> 升级为文件日志。所有路径自身绝不抛出。
/// </summary>
public static class CrashGuard
{
    private static ILogger? _logger;
    private static IServiceProvider? _services;
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandled;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static void AttachLogging(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("CrashGuard");
    }

    /// <summary>记录异常并尽量向用户通知；事件处理器与后台路径复用。</summary>
    public static void Report(Exception exception, string context)
    {
        try { Console.Error.WriteLine($"{context}: {exception}"); }
        catch { }
        try { _logger?.LogError(exception, "{Context}", context); }
        catch { }
        try
        {
            // 应用已退出或通知服务不可用时只记录；后台线程先调度到 UI 线程再展示。
            if (global::Avalonia.Application.Current is null) return;
            var notifier = _services?.GetService<NotificationService>();
            if (notifier is null) return;
            void Show() => notifier.ShowError($"{context}：{exception.Message}", "意外错误");
            if (Dispatcher.UIThread.CheckAccess()) Show();
            else Dispatcher.UIThread.Post(Show);
        }
        catch { }
    }

    private static void OnUiThreadUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception, "界面线程未处理异常");
        // 已记录并通知，保持应用存活；详细堆栈见应用日志。
        e.Handled = true;
    }

    private static void OnDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Report(ex, "未处理异常(进程即将终止)");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Report(e.Exception, "后台任务异常未被观察");
        e.SetObserved();
    }
}
