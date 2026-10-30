using System.Net.Sockets;
using System.Text;
using fhirhose.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace fhirhose.HL7;

public sealed class Hl7MllpClient : IHl7Client
{
    private const byte StartBlock = 0x0B;
    private const byte EndBlock = 0x1C;
    private const byte CarriageReturn = 0x0D;

    private readonly Hl7Options _options;
    private readonly ILogger<Hl7MllpClient> _logger;

    public Hl7MllpClient(
        IOptions<Hl7Options> options,
        ILogger<Hl7MllpClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Hl7Acknowledgement> SendAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("HL7 payload is empty.", nameof(payload));

        using var client = new TcpClient();

        using var connectTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        connectTimeout.CancelAfter(_options.ConnectTimeout);

        await client.ConnectAsync(
            _options.Host,
            _options.Port,
            connectTimeout.Token);

        client.NoDelay = true;

        await using var stream = client.GetStream();

        var normalizedPayload =
            payload.TrimEnd('\r', '\n') + '\r';

        var payloadBytes = Encoding.UTF8.GetBytes(normalizedPayload);

        var framedMessage = new byte[payloadBytes.Length + 3];

        framedMessage[0] = StartBlock;

        Buffer.BlockCopy(
            payloadBytes,
            0,
            framedMessage,
            1,
            payloadBytes.Length);

        framedMessage[^2] = EndBlock;
        framedMessage[^1] = CarriageReturn;

        await stream.WriteAsync(
            framedMessage,
            cancellationToken);

        await stream.FlushAsync(cancellationToken);

        _logger.LogDebug(
            "Sent {Length} HL7 bytes to {Host}:{Port}",
            framedMessage.Length,
            _options.Host,
            _options.Port);

        var acknowledgementPayload =
            await ReadMllpMessageAsync(stream, cancellationToken);

        return Hl7Acknowledgement.Parse(acknowledgementPayload);
    }

    private async Task<string> ReadMllpMessageAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var ackTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        ackTimeout.CancelAfter(_options.AcknowledgementTimeout);

        using var buffer = new MemoryStream();

        var singleByte = new byte[1];
        var insideMessage = false;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(
                singleByte,
                ackTimeout.Token);

            if (bytesRead == 0)
            {
                throw new IOException(
                    "The MLLP connection closed before an acknowledgement was received.");
            }

            var value = singleByte[0];

            if (!insideMessage)
            {
                if (value == StartBlock)
                    insideMessage = true;

                continue;
            }

            if (value == EndBlock)
            {
                // Consume the trailing carriage return when available.
                var trailingByte = new byte[1];

                var trailingRead = await stream.ReadAsync(
                    trailingByte,
                    ackTimeout.Token);

                if (trailingRead == 1 &&
                    trailingByte[0] != CarriageReturn)
                {
                    _logger.LogWarning(
                        "MLLP acknowledgement did not end with a carriage return");
                }

                break;
            }

            buffer.WriteByte(value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}