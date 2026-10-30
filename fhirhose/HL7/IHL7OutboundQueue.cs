using PathologyOrders.Models;

namespace fhirhose.Services;

public interface IHl7OutboundQueue
{
    Task<long> EnqueueAsync(
        Hl7MessageResult result,
        IReadOnlyCollection<PathologyOrder> orders,
        CancellationToken cancellationToken = default);

    void Signal();
}