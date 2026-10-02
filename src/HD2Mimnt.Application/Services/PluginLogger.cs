using HD2Mimnt.PluginSDK;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Implementation of IPluginLogger that bridges to ILogger
/// </summary>
public class PluginLogger : IPluginLogger
{
    private readonly ILogger _logger;
    private readonly string _pluginId;

    public PluginLogger(ILogger logger, string pluginId)
    {
        _logger = logger;
        _pluginId = pluginId;
    }

    public void Info(string message)
    {
        _logger.LogInformation("[Plugin:{PluginId}] {Message}", _pluginId, message);
    }

    public void Warning(string message)
    {
        _logger.LogWarning("[Plugin:{PluginId}] {Message}", _pluginId, message);
    }

    public void Error(string message, Exception? exception = null)
    {
        if (exception != null)
        {
            _logger.LogError(exception, "[Plugin:{PluginId}] {Message}", _pluginId, message);
        }
        else
        {
            _logger.LogError("[Plugin:{PluginId}] {Message}", _pluginId, message);
        }
    }

    public void Debug(string message)
    {
        _logger.LogDebug("[Plugin:{PluginId}] {Message}", _pluginId, message);
    }
}
