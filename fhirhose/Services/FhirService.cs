using System.Runtime.CompilerServices;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PathologyOrders.Configuration;
using Task = System.Threading.Tasks.Task;

namespace PathologyOrders.Services;

public sealed class FhirOrderService
{
    private readonly FhirClient _client;
    private readonly FhirOptions _options;
    private readonly ILogger<FhirOrderService> _logger;

    public FhirOrderService(FhirClient client, IOptions<FhirOptions> options, ILogger<FhirOrderService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ServiceRequest> GetOrdersAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var searchParameters = new SearchParams()
            .LimitTo(_options.PageSize);

        foreach (var parameter in _options.SearchParameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter))
            {
                searchParameters.Where(parameter);
            }
        }

        _logger.LogInformation("Querying ServiceRequest resources from {BaseUrl}", _options.BaseUrl);

        var bundle = await _client.SearchAsync<ServiceRequest>(searchParameters, cancellationToken);

        while (bundle is not null)
        {
            foreach (var serviceRequest in bundle.Entry
                         .Select(x => x.Resource)
                         .OfType<ServiceRequest>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return serviceRequest;
            }

            if (bundle.NextLink is null)
            {
                yield break;
            }

            bundle = await _client.ContinueAsync(bundle, PageDirection.Next, cancellationToken);
        }
    }
}