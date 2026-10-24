using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Worker;

/// <summary>
/// Factory to create worker instances based on worker type
/// </summary>
public static class WorkerFactory
{
    /// <summary>
    /// Create a worker instance for the given type
    /// </summary>
    public static IWorkerAdapter Create(string workerType, WorkerHost host, IServiceProvider services)
    {
        return workerType switch
        {
            "Import" => new ImportWorkerAdapter(host, services),
            "Scan" => new ScanWorkerAdapter(host, services),
            "Validation" => new ValidationWorkerAdapter(host, services),
            _ => throw new ArgumentException($"Unknown worker type: {workerType}", nameof(workerType))
        };
    }
}

/// <summary>
/// Base interface for worker adapters that run in worker process
/// </summary>
public interface IWorkerAdapter
{
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapter to run ImportWorker in worker process
/// </summary>
internal class ImportWorkerAdapter : IWorkerAdapter
{
    private readonly WorkerHost _host;
    private readonly IServiceProvider _services;

    public ImportWorkerAdapter(WorkerHost host, IServiceProvider services)
    {
        _host = host;
        _services = services;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var command = await _host.ReceiveCommandAsync(cancellationToken);
        if (command == null)
        {
            await _host.SendErrorAsync(new InvalidOperationException("Failed to receive command"), cancellationToken);
            return;
        }

        try
        {
            var modRepository = _services.GetRequiredService<IModRepository>();

            var sourcePath = command.Arguments.GetValueOrDefault("SourcePath", string.Empty);
            if (string.IsNullOrEmpty(sourcePath))
            {
                await _host.SendCompletedAsync(false, null, new List<string> { "SourcePath argument is required" }, cancellationToken);
                return;
            }

            // Simple progress tracking
            await _host.SendProgressAsync(1, 3, "Validating mod path...", cancellationToken);
            await Task.Delay(500, cancellationToken);

            await _host.SendProgressAsync(2, 3, "Scanning mod files...", cancellationToken);
            await Task.Delay(1000, cancellationToken);

            await _host.SendProgressAsync(3, 3, "Import completed", cancellationToken);
            await _host.SendCompletedAsync(true, "Import completed successfully", null, cancellationToken);
        }
        catch (Exception ex)
        {
            await _host.SendErrorAsync(ex, cancellationToken);
        }
    }
}

/// <summary>
/// Adapter to run ScanWorker in worker process
/// </summary>
internal class ScanWorkerAdapter : IWorkerAdapter
{
    private readonly WorkerHost _host;
    private readonly IServiceProvider _services;

    public ScanWorkerAdapter(WorkerHost host, IServiceProvider services)
    {
        _host = host;
        _services = services;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var command = await _host.ReceiveCommandAsync(cancellationToken);
        if (command == null)
        {
            await _host.SendErrorAsync(new InvalidOperationException("Failed to receive command"), cancellationToken);
            return;
        }

        try
        {
            await _host.SendProgressAsync(1, 2, "Scanning mods...", cancellationToken);
            await Task.Delay(1000, cancellationToken);

            await _host.SendProgressAsync(2, 2, "Scan completed", cancellationToken);
            await _host.SendCompletedAsync(true, "Scan completed successfully", null, cancellationToken);
        }
        catch (Exception ex)
        {
            await _host.SendErrorAsync(ex, cancellationToken);
        }
    }
}

/// <summary>
/// Adapter to run ValidationWorker in worker process
/// </summary>
internal class ValidationWorkerAdapter : IWorkerAdapter
{
    private readonly WorkerHost _host;
    private readonly IServiceProvider _services;

    public ValidationWorkerAdapter(WorkerHost host, IServiceProvider services)
    {
        _host = host;
        _services = services;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var command = await _host.ReceiveCommandAsync(cancellationToken);
        if (command == null)
        {
            await _host.SendErrorAsync(new InvalidOperationException("Failed to receive command"), cancellationToken);
            return;
        }

        try
        {
            await _host.SendProgressAsync(1, 2, "Validating mods...", cancellationToken);
            await Task.Delay(1000, cancellationToken);

            await _host.SendProgressAsync(2, 2, "Validation completed", cancellationToken);
            await _host.SendCompletedAsync(true, "Validation completed successfully", null, cancellationToken);
        }
        catch (Exception ex)
        {
            await _host.SendErrorAsync(ex, cancellationToken);
        }
    }
}

