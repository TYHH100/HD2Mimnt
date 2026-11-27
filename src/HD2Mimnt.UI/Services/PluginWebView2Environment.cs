using Microsoft.Web.WebView2.Core;

namespace HD2Mimnt.UI.Services;

/// <summary>所有插件 WebView2 页面共用的浏览器环境；用户数据位于当前临时目录。</summary>
public sealed class PluginWebView2Environment
{
    private readonly string _userDataFolder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreWebView2Environment? _environment;

    public PluginWebView2Environment(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
    }

    public async Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is not null) return _environment;
        Directory.CreateDirectory(_userDataFolder);
        await _gate.WaitAsync();
        try
        {
            return _environment ??= await CoreWebView2Environment.CreateAsync(null, _userDataFolder, null);
        }
        finally
        {
            _gate.Release();
        }
    }
}
