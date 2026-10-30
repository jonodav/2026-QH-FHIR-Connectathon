using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PathologyOrders.Services;

namespace fhirhose.Services;

public sealed class OrderImportWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderImportWorker> _logger;

    public OrderImportWorker(IServiceScopeFactory scopeFactory, ILogger<OrderImportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Pathology order polling started. Interval: {Interval}", PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ImportOrdersAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Pathology order import failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Pathology order polling stopped");
    }

    private async Task ImportOrdersAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var importer = scope.ServiceProvider.GetRequiredService<OrderImportService>();

        _logger.LogInformation("Starting pathology order import");

        await importer.ImportAsync(cancellationToken);
    }
}