using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NHapi.Base.Parser;
using NHapi.Model.V231.Message;
using PathologyOrders.Data;
using PathologyOrders.Models;

namespace fhirhose.Services;

public sealed class OrderTranslationService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<OrderTranslationService> _logger;
    private readonly PipeParser _pipeParser = new();
    private readonly IHl7OutboundQueue _outboundQueue;

    public OrderTranslationService(
        AppDbContext dbContext,
        ILogger<OrderTranslationService> logger, IHl7OutboundQueue outboundQueue)
    {
        _dbContext = dbContext;
        _logger = logger;
        _outboundQueue = outboundQueue;
    }

    public async Task TranslateOrders(
        CancellationToken cancellationToken = default)
    {
        var changedOrders = await _dbContext.PathologyOrders
            .Where(order =>
                order.RequisitionGroupNumber != null &&
                order.RequisitionGroupNumber != string.Empty &&
                (order.LastQueuedAtUtc == null ||
                 order.FhirLastUpdated > order.LastQueuedAtUtc))
            .OrderBy(order => order.RequisitionGroupNumber)
            .ThenBy(order => order.FhirLastUpdated)
            .ToListAsync(cancellationToken);

        var groupedOrders = changedOrders
            .GroupBy(order => order.RequisitionGroupNumber!);

        foreach (var requisitionGroup in groupedOrders)
        {
            try
            {
                var orders = requisitionGroup.ToList();

                var message = CreateMessage(orders);

                var result = CreateMessage(orders);

                await _outboundQueue.EnqueueAsync(
                    result,
                    orders,
                    cancellationToken);

                var queuedAtUtc = DateTime.UtcNow;

                foreach (var order in orders)
                    order.LastQueuedAtUtc = queuedAtUtc;

                await _dbContext.SaveChangesAsync(cancellationToken);

                foreach (var order in orders)
                    order.LastQueuedAtUtc = queuedAtUtc;

                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Created ORM message {MessageControlId} for requisition " +
                    "{RequisitionNumber}, containing {OrderCount} changed orders",
                    message.MessageControlId,
                    requisitionGroup.Key,
                    orders.Count);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to translate orders for requisition {RequisitionNumber}",
                    requisitionGroup.Key);

                // Entries in this group retain their previous LastQueuedAtUtc
                // and will therefore be retried during the next batch.
            }
        }
    }

    public Hl7MessageResult CreateMessage(
        IReadOnlyCollection<PathologyOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);

        if (orders.Count == 0)
        {
            throw new ArgumentException(
                "At least one pathology order is required.",
                nameof(orders));
        }

        ValidateRequisitionGroup(orders);

        var converter = new FhirOrmConverter();

        var sourceMessages = orders
            .Select(order =>
            {
                if (string.IsNullOrWhiteSpace(order.RawJson))
                {
                    throw new InvalidOperationException(
                        $"Order contains no FHIR JSON for requisition " +
                        $"{order.RequisitionGroupNumber}.");
                }

                return converter.ConvertJson(order.RawJson);
            })
            .ToList();

        var finalOrm = CombineMessages(sourceMessages);
        var payload = _pipeParser.Encode(finalOrm);

        return new Hl7MessageResult(
            MessageControlId:
                finalOrm.MSH.MessageControlID.Value ?? string.Empty,

            MessageType:
                finalOrm.MSH.MessageType.MessageType.Value ?? string.Empty,

            TriggerEvent:
                finalOrm.MSH.MessageType.TriggerEvent.Value ?? string.Empty,

            OrderControl:
                GetCombinedOrderControl(finalOrm),

            Payload:
                payload);
    }

    private ORM_O01 CombineMessages(
        IReadOnlyList<ORM_O01> sourceMessages)
    {
        if (sourceMessages.Count == 0)
        {
            throw new ArgumentException(
                "At least one ORM message is required.",
                nameof(sourceMessages));
        }

        /*
         * Keep the message-level and patient-level segments from the first
         * source message. Order groups from every source message are then
         * appended below.
         */
        var combinedSegments = SplitSegments(
                _pipeParser.Encode(sourceMessages[0]))
            .TakeWhile(segment => !IsOrderStart(segment))
            .ToList();

        var obrSequence = 1;

        foreach (var sourceMessage in sourceMessages)
        {
            var sourceSegments = SplitSegments(
                    _pipeParser.Encode(sourceMessage))
                .ToList();

            var firstOrderIndex = sourceSegments.FindIndex(IsOrderStart);

            if (firstOrderIndex < 0)
            {
                throw new InvalidOperationException(
                    "A converted ORM message did not contain an ORC segment.");
            }

            foreach (var segment in sourceSegments.Skip(firstOrderIndex))
            {
                if (IsObrSegment(segment))
                {
                    combinedSegments.Add(
                        SetSegmentField(
                            segment,
                            fieldNumber: 1,
                            value: obrSequence.ToString()));

                    obrSequence++;
                }
                else
                {
                    /*
                     * ORC-1 is intentionally preserved. This allows individual
                     * orders in the same message to use NW, XO, CA, etc.
                     */
                    combinedSegments.Add(segment);
                }
            }
        }

        var encodedMessage =
            string.Join('\r', combinedSegments) + '\r';

        var finalOrm = (ORM_O01)_pipeParser.Parse(encodedMessage);

        // This is a new outbound batch message, so assign a new control ID.
        finalOrm.MSH.MessageControlID.Value =
            Guid.NewGuid().ToString("N");

        finalOrm.MSH.DateTimeOfMessage.TimeOfAnEvent.Value =
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss+0000");

        return finalOrm;
    }

    private static void ValidateRequisitionGroup(
        IReadOnlyCollection<PathologyOrder> orders)
    {
        var requisitionNumbers = orders
            .Select(order => order.RequisitionGroupNumber)
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requisitionNumbers.Count != 1)
        {
            throw new InvalidOperationException(
                "All orders passed to CreateMessage must belong to the " +
                "same requisition group.");
        }
    }

    private static string GetCombinedOrderControl(
        ORM_O01 message)
    {
        var orderControls = Enumerable
            .Range(0, message.ORDERRepetitionsUsed)
            .Select(index =>
                message.GetORDER(index).ORC.OrderControl.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return orderControls.Count switch
        {
            0 => string.Empty,
            1 => orderControls[0]!,
            _ => "MIXED"
        };
    }

    private static List<string> SplitSegments(string message)
    {
        return message
            .Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool IsOrderStart(string segment)
    {
        return segment.StartsWith(
            "ORC|",
            StringComparison.Ordinal);
    }

    private static bool IsObrSegment(string segment)
    {
        return segment.StartsWith(
            "OBR|",
            StringComparison.Ordinal);
    }

    private static string SetSegmentField(
        string segment,
        int fieldNumber,
        string value)
    {
        var fields = segment.Split('|');

        if (fields.Length <= fieldNumber)
        {
            Array.Resize(
                ref fields,
                fieldNumber + 1);
        }

        fields[fieldNumber] = value;

        return string.Join('|', fields);
    }
}

public sealed record Hl7MessageResult(
    string MessageControlId,
    string MessageType,
    string TriggerEvent,
    string OrderControl,
    string Payload);

public enum OrderMessageAction
{
    New,
    Update,
    Cancel
}