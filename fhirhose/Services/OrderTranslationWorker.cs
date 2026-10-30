using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PathologyOrders.Services;

namespace fhirhose.Services;

public sealed class OrderTranslationWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderTranslationWorker> _logger;

    public OrderTranslationWorker(IServiceScopeFactory scopeFactory, ILogger<OrderTranslationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Pathology translation polling started. Interval: {Interval}", PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TranslateOrdersAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Order translation failed");
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

        _logger.LogInformation("Pathology translation stopped");
    }

    private async Task TranslateOrdersAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var translator = scope.ServiceProvider.GetRequiredService<OrderTranslationService>();

        _logger.LogInformation("Starting pathology translations");

        await translator.TranslateOrders(cancellationToken);
    }
}