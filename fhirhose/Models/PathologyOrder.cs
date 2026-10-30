namespace PathologyOrders.Models;

public sealed class PathologyOrder
{
    public long Id { get; set; }

    public required string FhirId { get; set; }

    public string? IdentifierSystem { get; set; }

    public string? IdentifierValue { get; set; }

    public string? Status { get; set; }

    public string? Intent { get; set; }

    public string? OrderCode { get; set; }

    public string? OrderCodeSystem { get; set; }

    public string? OrderDescription { get; set; }

    public string? PatientReference { get; set; }

    public string? EncounterReference { get; set; }

    public string? RequesterReference { get; set; }
    public string? RequisitionGroupNumber { get; set; }
    public string? RequisitionNumber { get; set; }

    public string? AuthoredOn { get; set; }

    public DateTime? FhirLastUpdated { get; set; }

    public required string RawJson { get; set; }

    public DateTime ImportedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    // Hash of the data relevant to HL7 conversion
    public required string SourceHash { get; set; }

    // Most recent source state converted into an outbox message
    public string? LastQueuedSourceHash { get; set; }

    public DateTime? LastQueuedAtUtc { get; set; }

    public ICollection<Hl7OutboundMessageOrder> OutboundMessageOrders { get; set; } = [];
}