using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public abstract partial class DarktidePageViewModelBase : ObservableObject
{
    private CancellationTokenSource? _operation;

    protected DarktidePageViewModelBase(NotificationService notifications) => Notifications = notifications;

    protected NotificationService Notifications { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    protected async Task RunAsync(Func<CancellationToken, Task> action, string? successKey = null,
        string? failTitleKey = null)
    {
        if (IsBusy) return;
        _operation?.Dispose();
        _operation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = UiLocalization.Current["Darktide.Common.Busy"];
        try
        {
            await action(_operation.Token);
            StatusText = UiLocalization.Current["Darktide.Status.Ready"];
            if (successKey != null) Notifications.ShowSuccess(UiLocalization.Current[successKey], UiLocalization.Current["Darktide.Title"]);
        }
        catch (OperationCanceledException)
        {
            StatusText = UiLocalization.Current["Darktide.Status.Cancelled"];
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            Notifications.ShowError(ex.Message, UiLocalization.Current[failTitleKey ?? "Darktide.Title"]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel() => _operation?.Cancel();

    [RelayCommand]
    private void CancelOperation() => Cancel();

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync();

    protected static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    protected IProgress<double> CreateUiProgress(Action<double> update)
        => new Progress<double>(value => OnUi(() => update(value)));

    protected static Task RunOffUiThreadAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        => Task.Run(() => action(cancellationToken), cancellationToken);

    protected static Task<T> RunOffUiThreadAsync<T>(Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
        => Task.Run(() => action(cancellationToken), cancellationToken);

    public abstract Task RefreshAsync(CancellationToken cancellationToken = default);
}
