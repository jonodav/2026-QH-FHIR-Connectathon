namespace PathologyOrders.Models;

public sealed class Hl7OutboundMessage
{
    public long Id { get; set; }

    public required string MessageControlId { get; set; }
    public required string MessageType { get; set; }
    public required string TriggerEvent { get; set; }
    public required string Hl7Version { get; set; }

    // NW, XO, CA or MIXED
    public required string OrderControl { get; set; }

    // Immutable encoded ER7 message
    public required string Payload { get; set; }

    public OutboundMessageStatus Status { get; set; }

    public int AttemptCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }

    public string? AcknowledgementCode { get; set; }
    public string? AcknowledgementText { get; set; }
    public string? AcknowledgementPayload { get; set; }

    public string? LastError { get; set; }

    public ICollection<Hl7OutboundMessageOrder> Orders { get; set; } = [];
}

public sealed class Hl7OutboundMessageOrder
{
    public long Hl7OutboundMessageId { get; set; }

    public Hl7OutboundMessage Hl7OutboundMessage { get; set; } = null!;

    public long PathologyOrderId { get; set; }

    public PathologyOrder PathologyOrder { get; set; } = null!;
}

public enum OutboundMessageStatus
{
    Pending = 0,
    Sending = 1,
    Acknowledged = 2,
    RetryPending = 3,
    Rejected = 4,
    DeadLetter = 5
}