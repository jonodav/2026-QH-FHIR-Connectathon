using System.Threading.Channels;
using fhirhose.HL7;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PathologyOrders.Data;
using PathologyOrders.Models;

namespace fhirhose.Services;

public sealed class OrderSendingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChannelReader<bool> _signalReader;
    private readonly Hl7Options _options;
    private readonly ILogger<OrderSendingWorker> _logger;

    public OrderSendingWorker(
        IServiceScopeFactory scopeFactory,
        Channel<bool> signalChannel,
        IOptions<Hl7Options> options,
        ILogger<OrderSendingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _signalReader = signalChannel.Reader;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation("HL7 outbound worker started");

        await RecoverInterruptedMessagesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await ProcessAvailableMessagesAsync(stoppingToken))
                {
                    // Continue immediately while due messages remain.
                }

                await WaitForSignalOrPollIntervalAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Unexpected failure in the HL7 outbound worker");

                await Task.Delay(
                    TimeSpan.FromSeconds(5),
                    stoppingToken);
            }
        }

        _logger.LogInformation("HL7 outbound worker stopped");
    }

    private async Task<bool> ProcessAvailableMessagesAsync(
        CancellationToken cancellationToken)
    {
        List<long> messageIds;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var dbContext =
                scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var now = DateTime.UtcNow;

            messageIds = await dbContext.Hl7OutboundMessages
                .Where(message =>
                    (message.Status == OutboundMessageStatus.Pending ||
                     message.Status == OutboundMessageStatus.RetryPending) &&
                    (message.NextAttemptAtUtc == null ||
                     message.NextAttemptAtUtc <= now))
                .OrderBy(message => message.CreatedAtUtc)
                .Take(_options.BatchSize)
                .Select(message => message.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var messageId in messageIds)
            await ProcessMessageAsync(messageId, cancellationToken);

        return messageIds.Count == _options.BatchSize;
    }

    private async Task ProcessMessageAsync(
        long messageId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var dbContext =
            scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var hl7Client =
            scope.ServiceProvider.GetRequiredService<IHl7Client>();

        var message = await dbContext.Hl7OutboundMessages
            .SingleOrDefaultAsync(
                x => x.Id == messageId,
                cancellationToken);

        if (message is null)
            return;

        if (message.Status is not
            (OutboundMessageStatus.Pending or
             OutboundMessageStatus.RetryPending))
        {
            return;
        }

        message.Status = OutboundMessageStatus.Sending;
        message.AttemptCount++;
        message.LastAttemptAtUtc = DateTime.UtcNow;
        message.NextAttemptAtUtc = null;
        message.LastError = null;

        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var acknowledgement = await hl7Client.SendAsync(
                message.Payload,
                cancellationToken);

            message.AcknowledgementCode = acknowledgement.Code;
            message.AcknowledgementText = acknowledgement.Text;
            message.AcknowledgementPayload = acknowledgement.Payload;

            if (acknowledgement.IsAccepted)
            {
                message.Status = OutboundMessageStatus.Acknowledged;
                message.AcknowledgedAtUtc = DateTime.UtcNow;

                _logger.LogInformation(
                    "HL7 message {ControlId} acknowledged with {AckCode}",
                    message.MessageControlId,
                    acknowledgement.Code);
            }
            else
            {
                // AE, AR, CE and CR are application-level failures.
                // Retrying unchanged content is normally inappropriate.
                message.Status = OutboundMessageStatus.Rejected;
                message.LastError =
                    $"Receiver returned {acknowledgement.Code}: " +
                    acknowledgement.Text;

                _logger.LogWarning(
                    "HL7 message {ControlId} rejected with {AckCode}: {Text}",
                    message.MessageControlId,
                    acknowledgement.Code,
                    acknowledgement.Text);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // Allow startup recovery to return Sending messages to RetryPending.
            throw;
        }
        catch (Exception exception)
        {
            ApplyRetry(message, exception);

            _logger.LogWarning(
                exception,
                "Attempt {Attempt} failed for HL7 message {ControlId}; status {Status}",
                message.AttemptCount,
                message.MessageControlId,
                message.Status);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ApplyRetry(
        Hl7OutboundMessage message,
        Exception exception)
    {
        message.LastError = exception.ToString();

        if (message.AttemptCount >= _options.MaximumAttempts)
        {
            message.Status = OutboundMessageStatus.DeadLetter;
            message.NextAttemptAtUtc = null;
            return;
        }

        message.Status = OutboundMessageStatus.RetryPending;

        var delay = CalculateRetryDelay(message.AttemptCount);

        message.NextAttemptAtUtc =
            DateTime.UtcNow.Add(delay);
    }

    private static TimeSpan CalculateRetryDelay(int attemptCount)
    {
        // 5s, 10s, 20s, 40s... capped at 15 minutes.
        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        var seconds = 5 * Math.Pow(2, exponent);

        return TimeSpan.FromSeconds(
            Math.Min(seconds, 15 * 60));
    }

    private async Task RecoverInterruptedMessagesAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var dbContext =
            scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var interruptedMessages = await dbContext.Hl7OutboundMessages
            .Where(x => x.Status == OutboundMessageStatus.Sending)
            .ToListAsync(cancellationToken);

        if (interruptedMessages.Count == 0)
            return;

        foreach (var message in interruptedMessages)
        {
            message.Status = OutboundMessageStatus.RetryPending;
            message.NextAttemptAtUtc = DateTime.UtcNow;
            message.LastError =
                "Sending was interrupted by application shutdown.";
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Recovered {Count} interrupted HL7 outbound messages",
            interruptedMessages.Count);
    }

    private async Task WaitForSignalOrPollIntervalAsync(
        CancellationToken cancellationToken)
    {
        using var delayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        var signalTask = _signalReader
            .ReadAsync(cancellationToken)
            .AsTask();

        var delayTask = Task.Delay(
            _options.PollInterval,
            delayCancellation.Token);

        var completedTask = await Task.WhenAny(
            signalTask,
            delayTask);

        if (completedTask == signalTask)
        {
            delayCancellation.Cancel();

            await signalTask;

            // Collapse multiple signals into one database scan.
            while (_signalReader.TryRead(out _))
            {
            }
        }
    }
}