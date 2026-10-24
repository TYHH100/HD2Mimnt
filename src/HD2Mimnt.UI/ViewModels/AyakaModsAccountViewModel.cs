using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.UI.ViewModels;

public sealed partial class AyakaModsAccountViewModel(AyakaModsAuthService service, AyakaModsOAuthService oauth,
    IBrowserLauncher browser) : ObservableObject
{
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyApiKeyCommand), nameof(ClearApiKeyCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _clientId = oauth.ClientId;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyApiKeyCommand), nameof(ClearApiKeyCommand), nameof(LoginCommand), nameof(LogoutCommand), nameof(ApplyClientIdCommand))]
    private bool _isLoggingIn;
    private CancellationTokenSource? _login;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public string RedirectUri => oauth.RedirectUri;
    public string OAuthStatus => UiLocalization.Current[oauth.IsSignedIn ? "Ayaka.OAuth.SignedIn" : oauth.IsConfigured ? "Ayaka.OAuth.SignedOut" : "Ayaka.OAuth.NotConfigured"];
    public string Status => UiLocalization.Current[service.IsAuthenticated ? "Ayaka.KeyConfigured" : "Ayaka.KeyMissing"];
    private bool CanChange() => !IsBusy && !IsLoggingIn;
    partial void OnIsBusyChanged(bool value)
    { LoginCommand.NotifyCanExecuteChanged(); LogoutCommand.NotifyCanExecuteChanged(); ApplyClientIdCommand.NotifyCanExecuteChanged(); }

    public async Task RestoreAsync()
    {
        try { await service.RestoreApiKeyAsync(); Message = ""; }
        catch { Message = UiLocalization.Current["Ayaka.RestoreFailed"]; }
        try { await oauth.RestoreAsync(); ClientId = oauth.ClientId; }
        catch { Message = UiLocalization.Current["Ayaka.OAuth.RestoreFailed"]; }
        OnPropertyChanged(nameof(Status));
        RefreshOAuthStatus();
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ApplyApiKeyAsync()
    {
        IsBusy = true;
        try
        {
            await service.ApplyApiKeyAsync(ApiKey);
            ApiKey = "";
            Message = UiLocalization.Current["Ayaka.KeyApplied"];
        }
        catch (ArgumentException) { Message = UiLocalization.Current["Ayaka.InvalidKey"]; }
        catch { Message = UiLocalization.Current["Ayaka.SaveFailed"]; }
        finally { IsBusy = false; OnPropertyChanged(nameof(Status)); }
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ClearApiKeyAsync()
    {
        IsBusy = true;
        try { await service.ClearApiKeyAsync(); ApiKey = ""; Message = ""; }
        catch { Message = UiLocalization.Current["Ayaka.SaveFailed"]; }
        finally { IsBusy = false; OnPropertyChanged(nameof(Status)); }
    }

    private void RefreshOAuthStatus()
    { OnPropertyChanged(nameof(OAuthStatus)); LoginCommand.NotifyCanExecuteChanged(); LogoutCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ApplyClientIdAsync()
    {
        IsBusy = true;
        try { await oauth.ApplyClientIdAsync(ClientId); ClientId = oauth.ClientId; Message = UiLocalization.Current["Ayaka.OAuth.ClientIdSaved"]; }
        catch (ArgumentException) { Message = UiLocalization.Current["Ayaka.OAuth.InvalidClientId"]; }
        catch (InvalidOperationException ex) { Message = ex.Message; }
        catch { Message = UiLocalization.Current["Ayaka.OAuth.SaveFailed"]; }
        finally { IsBusy = false; RefreshOAuthStatus(); }
    }

    private bool CanLogin() => CanChange() && !oauth.IsSignedIn;
    private bool CanLogout() => CanChange() && oauth.IsSignedIn;
    [RelayCommand(CanExecute = nameof(CanLogin))]
    private Task LoginAsync() => Completion = RunLoginAsync();
    private async Task RunLoginAsync()
    {
        using var cts = new CancellationTokenSource();
        _login = cts; IsLoggingIn = true; Message = UiLocalization.Current["Ayaka.OAuth.BrowserPrompt"];
        try
        {
            await oauth.LoginAsync(uri => browser.Open(uri.AbsoluteUri), cts.Token);
            Message = UiLocalization.Current["Ayaka.OAuth.LoginSucceeded"];
        }
        catch (OperationCanceledException) { Message = UiLocalization.Current["Ayaka.OAuth.Cancelled"]; }
        catch (AyakaModsOAuthException ex) { Message = ex.Message; }
        catch (InvalidOperationException ex) { Message = ex.Message; }
        catch { Message = UiLocalization.Current["Ayaka.OAuth.LoginFailed"]; }
        finally { _login = null; IsLoggingIn = false; RefreshOAuthStatus(); }
    }
    [RelayCommand(CanExecute = nameof(CanLogout))]
    private Task LogoutAsync() => Completion = RunLogoutAsync();
    private async Task RunLogoutAsync()
    {
        IsBusy = true;
        try { await oauth.LogoutAsync(); Message = UiLocalization.Current["Ayaka.OAuth.LoggedOut"]; }
        catch { Message = UiLocalization.Current["Ayaka.OAuth.SaveFailed"]; }
        finally { IsBusy = false; RefreshOAuthStatus(); }
    }
    [RelayCommand]
    private void CancelLogin() => _login?.Cancel();
    public async Task StopAsync() { _login?.Cancel(); await Completion; }
}
