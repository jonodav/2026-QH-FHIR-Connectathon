namespace fhirhose.Services;

public sealed class Hl7Options
{
    public const string SectionName = "Hl7";

    public required string Host { get; init; }

    public int Port { get; init; } = 2575;

    public TimeSpan ConnectTimeout { get; init; } =
        TimeSpan.FromSeconds(10);

    public TimeSpan AcknowledgementTimeout { get; init; } =
        TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; init; } =
        TimeSpan.FromSeconds(5);

    public int MaximumAttempts { get; init; } = 10;

    public int BatchSize { get; init; } = 20;
}