using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 插件安全事件上报：停用插件并写入一条 Blocked 执行记录，便于在界面上追溯。
/// </summary>
public interface IPluginSecurityReporter
{
    Task ReportViolationAsync(string pluginId, string reason, CancellationToken cancellationToken = default);
}

public sealed class PluginSecurityReporter : IPluginSecurityReporter
{
    public const string ViolationCommand = "security-violation";

    private readonly IPluginRepository _pluginRepository;
    private readonly IPluginExecutionRepository _executionRepository;
    private readonly ILogger<PluginSecurityReporter>? _logger;

    public PluginSecurityReporter(
        IPluginRepository pluginRepository,
        IPluginExecutionRepository executionRepository,
        ILogger<PluginSecurityReporter>? logger = null)
    {
        _pluginRepository = pluginRepository;
        _executionRepository = executionRepository;
        _logger = logger;
    }

    public async Task ReportViolationAsync(string pluginId, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            // 先停用再记录：即使写入历史失败，插件也不会在下次被自动拉起
            var plugin = await _pluginRepository.GetByIdAsync(pluginId, cancellationToken);
            if (plugin is { Enabled: true })
            {
                plugin.Enabled = false;
                await _pluginRepository.UpdateAsync(plugin, cancellationToken);
            }

            await _executionRepository.AddAsync(new PluginExecutionHistory
            {
                Id = Guid.NewGuid().ToString(),
                PluginId = pluginId,
                Command = ViolationCommand,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                Status = "Blocked",
                Error = reason
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "记录插件 {PluginId} 安全事件失败", pluginId);
        }
    }
}
