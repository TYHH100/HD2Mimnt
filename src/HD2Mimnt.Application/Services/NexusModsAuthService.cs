using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;

namespace HD2Mimnt.Application.Services;

/// <summary>共用认证状态、加密持久化和串行刷新；个人 Key 保留为开发测试入口。</summary>
public class NexusModsAuthService(IPlatformRegistry platforms, IPlatformCredentialStore? credentials = null,
    INexusModsOAuthClient? oauthClient = null, NexusModsOAuthOptions? options = null, TimeProvider? timeProvider = null,
    SettingsService? settings = null)
{
    private const string OAuthPrefix = "hd2mimnt-oauth-v1:";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly NexusModsOAuthOptions _defaultOptions = options ?? new();
    private NexusModsOAuthOptions _options = LoadOptions(options ?? new(), settings);
    private NexusModsOAuthTokens? _tokens;
    private NexusModsOAuthTokens? _pendingRefresh;
    private int _revision;
    public event EventHandler? AuthenticationChanged;
    public bool IsApiKeySaved { get; private set; }
    public bool IsOAuthSignedIn => _tokens != null;
    public string? UserName => _tokens?.UserName;
    public bool IsOAuthConfigured => !string.IsNullOrWhiteSpace(_options.ClientId);
    public string OAuthClientId => _options.ClientId;
    public string OAuthRedirectUri => _options.RedirectUri;

    private static NexusModsOAuthOptions LoadOptions(NexusModsOAuthOptions defaults, SettingsService? settings)
    {
        var clientId = settings?.GetSetting(SettingKeys.NexusOAuthTestClientId, "") ?? "";
        return string.IsNullOrEmpty(clientId) ? defaults : defaults with { ClientId = clientId };
    }

    /// <summary>仅保存公开 Client ID；空输入恢复发行/环境配置，令牌仍只保存在凭据存储。</summary>
    public async Task ApplyOAuthClientIdAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (clientId.Any(char.IsControl)) throw new ArgumentException("Client ID 不能包含换行或控制字符。", nameof(clientId));
        var value = clientId.Trim();
        var next = _defaultOptions with { ClientId = value.Length == 0 ? _defaultOptions.ClientId : value };
        if (next.ClientId.Length > 0) next.Validate();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens != null && next != _options)
                throw new InvalidOperationException("请先在设置的账号页退出 OAuth 登录，再更换 Client ID。");
            cancellationToken.ThrowIfCancellationRequested();
            if (settings != null) await settings.SetSettingAsync(SettingKeys.NexusOAuthTestClientId, value);
            _options = next;
            _revision++; // 使等待旧配置浏览器回调的登录失效。
        }
        finally { _gate.Release(); }
        AuthenticationChanged?.Invoke(this, EventArgs.Empty);
    }
    private NexusModsPlatformAdapter Platform =>
        platforms.GetPlatform("nexusmods") as NexusModsPlatformAdapter
        ?? throw new InvalidOperationException("Nexus Mods 平台未注册。");
    public bool IsAuthenticated => Platform.IsAuthenticated;
    public NexusModsRateLimit RateLimit => Platform.RateLimit;
    public event EventHandler? RateLimitChanged
    {
        add => Platform.RateLimitChanged += value;
        remove => Platform.RateLimitChanged -= value;
    }
    public Task RefreshRateLimitAsync(CancellationToken cancellationToken = default)
        => Platform.RefreshRateLimitAsync(cancellationToken);

    public async Task LoginAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        if (oauthClient == null) throw new InvalidOperationException("OAuth 客户端未配置。");
        if (!await _loginGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("已有 N 网登录正在进行，请先取消或完成登录。");
        try
        {
            NexusModsOAuthOptions loginOptions;
            int revision;
            await _gate.WaitAsync(cancellationToken);
            try { loginOptions = _options; loginOptions.Validate(); revision = _revision; }
            finally { _gate.Release(); }
            var tokens = await oauthClient.LoginAsync(loginOptions, openBrowser, cancellationToken);
            await _gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (revision != _revision) throw new OperationCanceledException("认证状态已更改，请重新登录。");
                await SaveTokensAsync(tokens, cancellationToken);
                ApplyTokens(tokens);
                _revision++;
            }
            finally { _gate.Release(); }
            AuthenticationChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _loginGate.Release(); }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (credentials != null) await credentials.DeleteApiKeyAsync("nexusmods", cancellationToken);
            ClearAuthentication();
            _revision++;
        }
        finally { _gate.Release(); }
        AuthenticationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ApplyApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var key = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl) || key.StartsWith(OAuthPrefix, StringComparison.Ordinal))
            throw new ArgumentException("请输入有效的 API 密钥，不能包含换行或控制字符。", nameof(apiKey));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (credentials != null) await credentials.SaveApiKeyAsync("nexusmods", key, cancellationToken);
            _tokens = null;
            _pendingRefresh = null;
            Platform.SetApiKey(key);
            IsApiKeySaved = credentials != null;
            _revision++;
        }
        finally { _gate.Release(); }
        AuthenticationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ClearApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens != null) return; // 测试 Key 清除按钮不能意外退出 OAuth 账户。
            if (credentials != null) await credentials.DeleteApiKeyAsync("nexusmods", cancellationToken);
            ClearAuthentication();
            _revision++;
        }
        finally { _gate.Release(); }
        AuthenticationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>兼容已有启动/备份恢复入口，同时读取旧 Key 或新的加密 OAuth 凭据。</summary>
    public async Task RestoreApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (credentials == null) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _revision++;
            _options = LoadOptions(_defaultOptions, settings);
            var secret = await credentials.ReadApiKeyAsync("nexusmods", cancellationToken);
            if (secret == null) ClearAuthentication();
            else if (secret.StartsWith(OAuthPrefix, StringComparison.Ordinal))
            {
                var saved = JsonSerializer.Deserialize<SavedOAuthCredential>(secret[OAuthPrefix.Length..])
                    ?? throw new JsonException();
                if (saved.ClientId != _options.ClientId || saved.RedirectUri != _options.RedirectUri || oauthClient == null)
                    throw new InvalidOperationException("已保存的 N 网登录与当前应用配置不匹配，请重新登录。");
                if (string.IsNullOrWhiteSpace(saved.Tokens.AccessToken) || string.IsNullOrWhiteSpace(saved.Tokens.RefreshToken) ||
                    string.IsNullOrWhiteSpace(saved.Tokens.UserName) || string.IsNullOrWhiteSpace(saved.Tokens.UserId))
                    throw new JsonException();
                ApplyTokens(saved.Tokens); // 不在启动时访问网络；请求前按需刷新。
            }
            else
            {
                if (string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl))
                    throw new InvalidOperationException("已保存的 API 密钥无效，请重新填写。");
                _tokens = null;
                _pendingRefresh = null;
                Platform.SetApiKey(secret);
                IsApiKeySaved = true;
            }
        }
        catch { ClearAuthentication(); throw; }
        finally
        {
            _gate.Release();
            AuthenticationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyTokens(NexusModsOAuthTokens tokens)
    {
        _tokens = tokens;
        _pendingRefresh = null;
        IsApiKeySaved = false;
        Platform.SetOAuthAuthentication(GetAccessTokenAsync);
    }

    private void ClearAuthentication()
    {
        _tokens = null;
        _pendingRefresh = null;
        IsApiKeySaved = false;
        Platform.ClearAuthentication();
    }

    private Task SaveTokensAsync(NexusModsOAuthTokens tokens, CancellationToken token)
        => credentials?.SaveApiKeyAsync("nexusmods", OAuthPrefix + JsonSerializer.Serialize(new SavedOAuthCredential
        { ClientId = _options.ClientId, RedirectUri = _options.RedirectUri, Tokens = tokens }), token) ?? Task.CompletedTask;

    private async Task<string> GetAccessTokenAsync(CancellationToken token)
    {
        var changed = false;
        await _gate.WaitAsync(token);
        try
        {
            if (_pendingRefresh is { } pending)
            {
                // 磁盘暂时失败后先补存旋转令牌，不能重复使用旧 Refresh Token。
                await SaveTokensAsync(pending, token);
                _tokens = pending;
                _pendingRefresh = null;
                changed = true;
            }
            var current = _tokens ?? throw new NexusModsOAuthException("N 网登录状态已更改，请重试操作。", true);
            if (current.ExpiresAt > _time.GetUtcNow().AddSeconds(60)) return current.AccessToken;
            try
            {
                var refreshed = await oauthClient!.RefreshAsync(_options, current, token);
                _pendingRefresh = refreshed;
                await SaveTokensAsync(refreshed, token);
                _tokens = refreshed;
                _pendingRefresh = null;
                changed = true;
                return refreshed.AccessToken;
            }
            catch (NexusModsOAuthException ex) when (ex.RequiresLogin)
            {
                ClearAuthentication();
                _revision++;
                changed = true;
                if (credentials != null) await credentials.DeleteApiKeyAsync("nexusmods", token);
                throw;
            }
        }
        finally
        {
            _gate.Release();
            if (changed) AuthenticationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class SavedOAuthCredential
    {
        public required string ClientId { get; init; }
        public required string RedirectUri { get; init; }
        public required NexusModsOAuthTokens Tokens { get; init; }
        public override string ToString() => "Nexus Mods OAuth credential [redacted]";
    }
}
