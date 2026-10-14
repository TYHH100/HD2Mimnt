using System.Net;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.Services;

public sealed record ModPlatformErrorMessage(Exception Exception, string Operation)
{
    public string Localize() => ModPlatformErrorFormatter.Format(Exception, Operation, localize: true);
}

internal static class ModPlatformErrorFormatter
{
    public static string Format(Exception exception, string operation, bool localize = false)
    {
        string Text(string value) => localize ? UiLocalization.Current.Translate(value) : value;
        if (exception is not HttpRequestException http) return Text(exception.Message);
        var platformError = exception as PlatformRequestException;
        operation = platformError?.Operation ?? operation;
        var source = platformError?.PlatformName ?? "服务器";
        var heading = $"{operation}失败" + (http.StatusCode is { } status ? $"（HTTP {(int)status}）" : "") + "。";
        if (localize) heading = http.StatusCode is { } code
            ? UiLocalization.Current.Format("Browse.HttpError", Text(operation), (int)code)
            : UiLocalization.Current.Format("Browse.OperationError", Text(operation));
        var guidance = http.StatusCode switch
        {
            HttpStatusCode.Gone when platformError?.Operation == "获取网页授权下载链接" =>
                "网页临时下载授权已过期。请到 N 网文件页面重新点击“Mod manager download”，粘贴新的 nxm:// 链接后重试。",
            HttpStatusCode.BadRequest when platformError?.Operation == "获取网页授权下载链接" =>
                "N 网认为临时授权与账户或文件不匹配。请使用当前认证账户重新登录网页，并完整复制所选文件的 nxm:// 链接。",
            HttpStatusCode.Forbidden when platformError?.Operation == "获取网页授权下载链接" =>
                "N 网拒绝当前网页授权。请使用同一账户在文件页面重新点击“Mod manager download”获取临时链接后重试。",
            HttpStatusCode.Unauthorized when platformError != null => "服务器未接受当前认证。请在“设置 → 账号”重新登录 N 网；个人 Key 测试请在开发工具重新填写并应用有效密钥。",
            HttpStatusCode.Unauthorized => "下载服务器未接受此下载链接的授权。请重新检查更新以获取新的下载链接后重试。",
            HttpStatusCode.Forbidden when platformError?.Operation == "获取下载链接" =>
                "N 网拒绝直接提供下载链接。免费账户请启用“网页授权下载”，到文件网页点击“Mod manager download”，再粘贴 nxm:// 临时链接。其他情况请查看下方服务器说明。",
            HttpStatusCode.Forbidden when platformError?.Operation == "获取会员下载链接" =>
                "N 网拒绝会员下载。请确认 Premium 会员仍有效，然后重新点击下载。",
            HttpStatusCode.Forbidden when platformError != null =>
                "N 网拒绝访问此信息。请确认模组或文件仍对你的账户开放，并根据下方服务器说明检查 API 访问限制。仅凭 HTTP 403 无法确定是密钥问题还是权限问题。",
            HttpStatusCode.Forbidden => "下载服务器拒绝访问文件。请重新检查更新以获取新的下载链接后重试。",
            HttpStatusCode.NotFound => "服务器未找到该模组或文件。请确认模组链接正确、文件未被删除，然后重新检查更新。",
            HttpStatusCode.TooManyRequests => "请求次数已达限制。请稍后再试，避免连续点击检查或下载。",
            null => "请求未收到有效的 HTTP 响应。请检查网络或代理设置后重试。",
            _ => "服务器未能完成请求。请稍后重试；若持续失败，请保留此状态码和服务器说明。"
        };
        var reason = platformError?.ServerReason is { Length: > 0 } detail
            ? $"\n{source} 返回的原因：{detail}"
            : "\n服务器未提供具体原因。";
        if (localize) reason = platformError?.ServerReason is { Length: > 0 } serverDetail
            ? "\n" + UiLocalization.Current.Format("Browse.ServerReason", Text(source), serverDetail)
            : UiLocalization.Current["UI.TheServerDidNotProvideASpecificReason"];
        return heading + "\n" + Text(guidance) + reason;
    }
}
