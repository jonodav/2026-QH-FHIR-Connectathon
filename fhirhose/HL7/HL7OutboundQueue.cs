using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PathologyOrders.Data;
using PathologyOrders.Models;

namespace fhirhose.Services;

public sealed class Hl7OutboundQueue : IHl7OutboundQueue
{
    private readonly AppDbContext _dbContext;
    private readonly ChannelWriter<bool> _signalWriter;

    public Hl7OutboundQueue(AppDbContext dbContext, Channel<bool> signalChannel)
    {
        _dbContext = dbContext;
        _signalWriter = signalChannel.Writer;
    }

    public async Task<long> EnqueueAsync(
        Hl7MessageResult result,
        IReadOnlyCollection<PathologyOrder> orders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(orders);

        if (orders.Count == 0)
        {
            throw new ArgumentException(
                "At least one pathology order is required.",
                nameof(orders));
        }

        var existingId = await _dbContext.Hl7OutboundMessages
            .Where(x => x.MessageControlId == result.MessageControlId)
            .Select(x => (long?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (existingId.HasValue)
        {
            Signal();
            return existingId.Value;
        }

        var message = new Hl7OutboundMessage
        {
            MessageControlId = result.MessageControlId,
            MessageType = result.MessageType,
            TriggerEvent = result.TriggerEvent,
            Hl7Version = "2.3.1",
            OrderControl = result.OrderControl,
            Payload = result.Payload,

            Status = OutboundMessageStatus.Pending,
            AttemptCount = 0,
            CreatedAtUtc = DateTime.UtcNow,
            NextAttemptAtUtc = DateTime.UtcNow,

            Orders = orders
                .Select(order => new Hl7OutboundMessageOrder
                {
                    PathologyOrderId = order.Id
                })
                .ToList()
        };

        _dbContext.Hl7OutboundMessages.Add(message);
        await _dbContext.SaveChangesAsync(cancellationToken);

        Signal();

        return message.Id;
    }

    public void Signal()
    {
        // A missed signal is harmless because the worker also polls the DB.
        _signalWriter.TryWrite(true);
    }
}