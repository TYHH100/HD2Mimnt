using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public partial class NexusModsAccountViewModel : ObservableObject, IDisposable
{
    private readonly NexusModsAuthService _auth;
    private readonly IBrowserLauncher _browser;
    private CancellationTokenSource? _login;
    private CancellationTokenSource? _rateRefresh;
    private Task _rateRefreshTask = Task.CompletedTask;
    private bool _disposed;
    public Task Completion { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _hasAuthentication;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isRefreshingRateLimit;
    [ObservableProperty] private object _rateLimitMessage = "";
    public string HourlyRateLimitText => FormatQuota("Nexus.HourlyQuota", _auth.RateLimit.HourlyRemaining, _auth.RateLimit.HourlyLimit);
    public string DailyRateLimitText => FormatQuota("Nexus.DailyQuota", _auth.RateLimit.DailyRemaining, _auth.RateLimit.DailyLimit);
    public string HourlyResetText => FormatDate("Nexus.HourlyReset", _auth.RateLimit.HourlyReset);
    public string DailyResetText => FormatDate("Nexus.DailyReset", _auth.RateLimit.DailyReset);
    public string RateLimitUpdatedText => FormatDate("Nexus.QuotaUpdated", _auth.RateLimit.UpdatedAt);
    public string RateLimitRetryText => _auth.RateLimit.RetryAt is { } date ? FormatDate("Nexus.RetryAt", date) : "";

    private static string FormatQuota(string key, int? remaining, int? limit) => UiLocalization.Current.Format(key,
        remaining?.ToString(UiLocalization.Current.Culture) ?? UiLocalization.Current["Nexus.QuotaUnknown"],
        limit?.ToString(UiLocalization.Current.Culture) ?? UiLocalization.Current["Nexus.QuotaUnknown"]);
    private static string FormatDate(string key, DateTimeOffset? date) => UiLocalization.Current.Format(key,
        date?.ToLocalTime().ToString("g", UiLocalization.Current.Culture) ?? UiLocalization.Current["Nexus.QuotaUnknown"]);

    public NexusModsAccountViewModel(NexusModsAuthService auth, IBrowserLauncher browser)
    {
        _auth = auth;
        _browser = browser;
        _auth.AuthenticationChanged += AuthChanged;
        _auth.RateLimitChanged += RateChanged;
        UiLocalization.Current.PropertyChanged += LanguageChanged;
        RefreshStatus();
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UiLocalization.Revision)) RateChanged(sender, EventArgs.Empty);
    }

    private void RateChanged(object? sender, EventArgs args)
    {
        void Update()
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(HourlyRateLimitText)); OnPropertyChanged(nameof(DailyRateLimitText));
            OnPropertyChanged(nameof(HourlyResetText)); OnPropertyChanged(nameof(DailyResetText));
            OnPropertyChanged(nameof(RateLimitUpdatedText)); OnPropertyChanged(nameof(RateLimitRetryText));
        }
        if (Dispatcher.UIThread.CheckAccess()) Update(); else Dispatcher.UIThread.Post(Update);
    }

    private void AuthChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshStatus();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) RefreshStatus(); });
    }

    public void RefreshStatus()
    {
        IsSignedIn = _auth.IsOAuthSignedIn;
        HasAuthentication = _auth.IsAuthenticated;
        Status = IsSignedIn ? $"已登录 N 网：{_auth.UserName}" : HasAuthentication ? "正在使用个人 API Key 测试。" :
            _auth.IsOAuthConfigured ? "尚未登录 N 网。" : "OAuth 应用申请中；获批后配置 Client ID 即可登录。";
        LoginCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();
        RefreshRateLimitCommand.NotifyCanExecuteChanged();
        RateChanged(this, EventArgs.Empty);
    }

    partial void OnIsBusyChanged(bool value)
    {
        LoginCommand.NotifyCanExecuteChanged(); LogoutCommand.NotifyCanExecuteChanged(); CancelLoginCommand.NotifyCanExecuteChanged();
        RefreshRateLimitCommand.NotifyCanExecuteChanged();
    }
    private bool CanLogin() => !IsBusy && !IsSignedIn;
    private bool CanLogout() => !IsBusy && HasAuthentication;
    private bool CanCancel() => IsBusy;
    private bool CanRefreshRateLimit() => !IsRefreshingRateLimit && !IsBusy && HasAuthentication;
    partial void OnIsRefreshingRateLimitChanged(bool value) => RefreshRateLimitCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanRefreshRateLimit))]
    private Task RefreshRateLimitAsync() => _rateRefreshTask = RunRateRefreshAsync();

    private async Task RunRateRefreshAsync()
    {
        using var cts = new CancellationTokenSource();
        _rateRefresh = cts;
        IsRefreshingRateLimit = true;
        RateLimitMessage = "";
        try { await _auth.RefreshRateLimitAsync(cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RateLimitMessage = new ModPlatformErrorMessage(ex, "刷新 N 网请求额度"); }
        finally { _rateRefresh = null; IsRefreshingRateLimit = false; RateChanged(this, EventArgs.Empty); }
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task LoginAsync() => Completion = RunLoginAsync();

    private async Task RunLoginAsync()
    {
        using var cts = new CancellationTokenSource();
        _login = cts;
        IsBusy = true;
        Message = "请在系统浏览器中完成 N 网授权。";
        try
        {
            await _auth.LoginAsync(uri => _browser.Open(uri.AbsoluteUri), cts.Token);
            Message = "N 网登录成功，凭据已在本机加密保存。";
        }
        catch (OperationCanceledException) { Message = "N 网登录已取消。"; }
        catch (NexusModsOAuthException ex) { Message = ex.Message; }
        catch (InvalidOperationException ex) { Message = ex.Message; }
        catch { Message = "N 网登录失败，请检查网络或本机凭据存储后重试。"; }
        finally { _login = null; IsBusy = false; RefreshStatus(); }
    }

    [RelayCommand(CanExecute = nameof(CanLogout))]
    private Task LogoutAsync() => Completion = RunLogoutAsync();

    private async Task RunLogoutAsync()
    {
        IsBusy = true;
        try
        {
            await _auth.LogoutAsync();
            Message = "已退出 N 网并删除本机保存的凭据。";
        }
        catch { Message = "凭据删除失败，登录状态保持不变，请重试。"; }
        finally { IsBusy = false; RefreshStatus(); }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelLogin() => _login?.Cancel();

    [RelayCommand]
    private void ManageAuthorization()
    {
        try { _browser.Open("https://users.nexusmods.com/oauth/authorized_applications"); }
        catch { Message = "无法打开系统浏览器，请重试。"; }
    }

    public async Task StopAsync() { _login?.Cancel(); _rateRefresh?.Cancel(); await Completion; await _rateRefreshTask; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _auth.AuthenticationChanged -= AuthChanged;
        _auth.RateLimitChanged -= RateChanged;
        UiLocalization.Current.PropertyChanged -= LanguageChanged;
        _login?.Cancel();
        _rateRefresh?.Cancel();
    }
}
