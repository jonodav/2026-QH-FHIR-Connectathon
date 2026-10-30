using NHapi.Base.Parser;
using NHapi.Base.Util;

namespace fhirhose.HL7;

/// <summary>
/// Sends an HL7 v2 message to the receiving system and returns its acknowledgement.
/// </summary>
public interface IHl7Client
{
    Task<Hl7Acknowledgement> SendAsync(
        string payload,
        CancellationToken cancellationToken = default);
}

public sealed record Hl7Acknowledgement(
    string Code,
    string? MessageControlId,
    string? Text,
    string Payload)
{
    public bool IsAccepted =>
        Code is "AA" or "CA";

    public static Hl7Acknowledgement Parse(string payload)
    {
        var acknowledgement = new PipeParser().Parse(payload);
        var terser = new Terser(acknowledgement);

        var code = terser.Get("/MSA-1");

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException(
                "The received HL7 response does not contain MSA-1.");
        }

        return new Hl7Acknowledgement(
            Code: code,
            MessageControlId: terser.Get("/MSA-2"),
            Text: terser.Get("/MSA-3"),
            Payload: payload);
    }
}
