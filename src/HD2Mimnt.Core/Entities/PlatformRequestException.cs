using System.Net;

namespace HD2Mimnt.Core.Entities;

/// <summary>平台请求失败的操作与服务端说明，不包含请求地址或认证信息。</summary>
public sealed class PlatformRequestException(string platformName, string operation,
    HttpStatusCode statusCode, string? serverReason)
    : HttpRequestException($"{platformName} API ({(int)statusCode}): {serverReason}", null, statusCode)
{
    public string PlatformName { get; } = platformName;
    public string Operation { get; } = operation;
    public string? ServerReason { get; } = serverReason;
}
