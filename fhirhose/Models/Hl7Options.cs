namespace fhirhose.Services;

public sealed class Hl7Options
{
    public const string SectionName = "Hl7";

    public Hl7Transport Transport { get; init; } = Hl7Transport.Mllp;

    // MLLP settings
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 2575;

    public TimeSpan ConnectTimeout { get; init; } =
        TimeSpan.FromSeconds(10);

    public TimeSpan AcknowledgementTimeout { get; init; } =
        TimeSpan.FromSeconds(30);

    // HTTP settings: the payload is POSTed to HttpUrl
    public string HttpUrl { get; init; } = string.Empty;

    public string HttpContentType { get; init; } = "x-application/hl7-v2+er7";

    public TimeSpan PollInterval { get; init; } =
        TimeSpan.FromSeconds(5);

    public int MaximumAttempts { get; init; } = 10;

    public int BatchSize { get; init; } = 20;
}

public enum Hl7Transport
{
    Mllp,
    Http
}
