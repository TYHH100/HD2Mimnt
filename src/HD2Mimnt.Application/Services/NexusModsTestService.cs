using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>保留旧开发工具和启动入口；与正式 OAuth 登录共用同一认证服务。</summary>
public sealed class NexusModsTestService(IPlatformRegistry platforms, IPlatformCredentialStore? credentials = null,
    INexusModsOAuthClient? oauthClient = null, NexusModsOAuthOptions? options = null, TimeProvider? timeProvider = null,
    SettingsService? settings = null)
    : NexusModsAuthService(platforms, credentials, oauthClient, options, timeProvider, settings);
