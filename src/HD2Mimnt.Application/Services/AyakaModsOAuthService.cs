using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>User OAuth state is independent from the read-only tool key used for browsing/downloading.</summary>
public sealed class AyakaModsOAuthService(IAyakaModsOAuthClient client, IPlatformCredentialStore credentials,
    SettingsService settings, AyakaModsOAuthOptions? defaults = null, TimeProvider? time = null)
{
    private const string CredentialId = "ayakamods-oauth";
    private const string ClientIdSetting = "Ayaka.OAuth.ClientId";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly AyakaModsOAuthOptions _defaults = defaults ?? new();
    private AyakaModsOAuthOptions _options = LoadOptions(defaults ?? new(), settings);
    private AyakaModsOAuthTokens? _tokens;
    private AyakaModsOAuthTokens? _pendingRefresh;
    private int _revision;
    public bool IsSignedIn => _tokens != null;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ClientId);
    public string ClientId => _options.ClientId;
    public string RedirectUri => _options.RedirectUri;
    private static AyakaModsOAuthOptions LoadOptions(AyakaModsOAuthOptions defaults, SettingsService settings)
    {
        var id = settings.GetSetting(ClientIdSetting, "");
        return string.IsNullOrWhiteSpace(id) ? defaults : defaults with { ClientId = id };
    }

    public async Task ApplyClientIdAsync(string value, CancellationToken token = default)
    {
        if (value.Any(char.IsControl)) throw new ArgumentException("Client ID 不能包含换行或控制字符。");
        var id = value.Trim();
        var next = _defaults with { ClientId = id.Length > 0 ? id : _defaults.ClientId };
        if (next.ClientId.Length > 0) next.Validate();
        await _gate.WaitAsync(token);
        try
        {
            if (_tokens != null && next != _options) throw new InvalidOperationException("请先退出 A 网 OAuth 登录，再更换 Client ID。");
            await settings.SetSettingAsync(ClientIdSetting, id);
            _options = next; _revision++;
        }
        finally { _gate.Release(); }
    }

    public async Task LoginAsync(Action<Uri> openBrowser, CancellationToken token = default)
    {
        if (!await _loginGate.WaitAsync(0, token)) throw new InvalidOperationException("已有 A 网登录正在进行。");
        try
        {
            AyakaModsOAuthOptions options;
            int revision;
            await _gate.WaitAsync(token);
            try { options = _options; options.Validate(); revision = _revision; }
            finally { _gate.Release(); }
            var result = await client.LoginAsync(options, openBrowser, token);
            await _gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (_revision != revision) throw new OperationCanceledException("A 网认证配置已更改，请重新登录。");
                await SaveAsync(result, token);
                _tokens = result; _pendingRefresh = null; _revision++;
            }
            finally { _gate.Release(); }
        }
        finally { _loginGate.Release(); }
    }

    /// <summary>For future player-identity API actions only; never replaces the tool key.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var tokens = _tokens ?? throw new InvalidOperationException("请先登录 A 网 OAuth 账户。");
            if (_pendingRefresh != null)
            {
                await SaveAsync(_pendingRefresh, token);
                _tokens = tokens = _pendingRefresh; _pendingRefresh = null;
            }
            if (tokens.ExpiresAt <= _time.GetUtcNow().AddSeconds(60))
            {
                try { _pendingRefresh = await client.RefreshAsync(_options, tokens, token); }
                catch (AyakaModsOAuthException ex) when (ex.RequiresLogin)
                {
                    await credentials.DeleteApiKeyAsync(CredentialId, token);
                    _tokens = null; _revision++; throw;
                }
                await SaveAsync(_pendingRefresh, token);
                _tokens = tokens = _pendingRefresh; _pendingRefresh = null;
            }
            return tokens.AccessToken;
        }
        finally { _gate.Release(); }
    }

    private Task SaveAsync(AyakaModsOAuthTokens tokens, CancellationToken token) =>
        credentials.SaveApiKeyAsync(CredentialId, JsonSerializer.Serialize(new Saved(_options.ClientId, _options.RedirectUri, tokens)), token);

    public async Task LogoutAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            // Public clients cannot revoke/introspect tokens. Local removal is the documented sign-out path.
            await credentials.DeleteApiKeyAsync(CredentialId, token);
            _tokens = _pendingRefresh = null; _revision++;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _revision++; _tokens = _pendingRefresh = null;
            _options = LoadOptions(_defaults, settings);
            var secret = await credentials.ReadApiKeyAsync(CredentialId, token);
            if (secret == null) return;
            var saved = JsonSerializer.Deserialize<Saved>(secret) ?? throw new JsonException();
            if (saved.ClientId != _options.ClientId || saved.RedirectUri != _options.RedirectUri ||
                string.IsNullOrWhiteSpace(saved.Tokens.AccessToken) || string.IsNullOrWhiteSpace(saved.Tokens.RefreshToken) ||
                saved.Tokens.AccessToken.Any(char.IsControl) || saved.Tokens.RefreshToken.Any(char.IsControl))
                throw new InvalidDataException("已保存的 A 网 OAuth 凭据与当前配置不匹配，请重新登录。");
            _tokens = saved.Tokens;
        }
        finally { _gate.Release(); }
    }
    private sealed record Saved(string ClientId, string RedirectUri, AyakaModsOAuthTokens Tokens);
}
