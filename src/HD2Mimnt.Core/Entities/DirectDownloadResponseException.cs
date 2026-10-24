namespace HD2Mimnt.Core.Entities;

public enum DirectDownloadResponseFailure { LoginRequired, AccessDenied, ExpiredLink, WebPage, ApiResponse, ServerError }

/// <summary>A non-file response. Never retains the response body, signed URL or authentication information.</summary>
public sealed class DirectDownloadResponseException(DirectDownloadResponseFailure failure) : IOException(MessageFor(failure))
{
    public DirectDownloadResponseFailure Failure { get; } = failure;
    public bool RequiresBrowser => Failure != DirectDownloadResponseFailure.ServerError;

    private static string MessageFor(DirectDownloadResponseFailure failure) => failure switch
    {
        DirectDownloadResponseFailure.LoginRequired => "此链接需要先在浏览器登录账号并完成验证。请使用验证后的实际下载地址，或由浏览器下载文件后导入。",
        DirectDownloadResponseFailure.AccessDenied => "网站拒绝访问该链接。请在浏览器确认登录与下载权限，或重新获取有效的下载地址。",
        DirectDownloadResponseFailure.ExpiredLink => "下载链接或验证信息已过期，请在浏览器重新获取有效的下载地址。",
        DirectDownloadResponseFailure.WebPage => "链接返回了网页而不是文件，请在浏览器完成登录或验证后使用实际下载地址。",
        DirectDownloadResponseFailure.ApiResponse => "链接返回了接口数据，而不是下载文件。请在浏览器确认下载状态并获取实际下载地址。",
        _ => "下载接口返回了错误信息，没有提供可下载的文件。请稍后重试或在浏览器检查下载状态。"
    };
}
