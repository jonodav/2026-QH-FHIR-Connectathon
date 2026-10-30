using System.Net.Http.Headers;
using System.Text;
using fhirhose.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace fhirhose.HL7;

/// <summary>
/// Sends HL7 v2 messages by POSTing the ER7 (pipe-delimited) payload to an HTTP endpoint.
/// </summary>
public sealed class Hl7HttpClient : IHl7Client
{
    private readonly HttpClient _httpClient;
    private readonly Hl7Options _options;
    private readonly ILogger<Hl7HttpClient> _logger;

    public Hl7HttpClient(
        HttpClient httpClient,
        IOptions<Hl7Options> options,
        ILogger<Hl7HttpClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Hl7Acknowledgement> SendAsync(
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("HL7 payload is empty.", nameof(payload));

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(_options.AcknowledgementTimeout);

        var normalizedPayload =
            payload.TrimEnd('\r', '\n') + '\r';

        using var content = new StringContent(normalizedPayload, Encoding.UTF8);

        content.Headers.ContentType =
            new MediaTypeHeaderValue(_options.HttpContentType) { CharSet = "utf-8" };

        using var response = await _httpClient.PostAsync(
            _options.HttpUrl,
            content,
            timeout.Token);

        var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);

        _logger.LogDebug(
            "Posted {Length} HL7 characters to {Url}; response {StatusCode}",
            normalizedPayload.Length,
            _options.HttpUrl,
            (int)response.StatusCode);

        // Transport failures are thrown so the sending worker retries the message.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HL7 HTTP endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}",
                inner: null,
                response.StatusCode);
        }

        if (responseBody.TrimStart().StartsWith("MSH", StringComparison.Ordinal))
            return Hl7Acknowledgement.Parse(responseBody);

        // The endpoint accepted the message but did not return an HL7 ACK.
        return new Hl7Acknowledgement(
            Code: "AA",
            MessageControlId: null,
            Text: $"HTTP {(int)response.StatusCode} with no HL7 acknowledgement",
            Payload: responseBody);
    }
}
