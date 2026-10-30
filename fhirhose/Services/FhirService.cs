using System.Runtime.CompilerServices;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PathologyOrders.Configuration;
using Task = Hl7.Fhir.Model.Task;

namespace PathologyOrders.Services;

/// <summary>
/// A single ServiceRequest together with every resource needed to translate it,
/// packaged as a self-contained collection Bundle.
/// </summary>
public sealed record FhirOrder(
    ServiceRequest ServiceRequest,
    Task DiagnosticRequestTask,
    Task GroupTask,
    Bundle Bundle);

/// <summary>
/// Retrieves AU eRequesting orders starting from the filler's group Task
/// (au-erequesting-task-group) and pulls in the diagnostic request Tasks,
/// ServiceRequests and the resources they reference.
/// </summary>
public sealed class FhirOrderService
{
    private const string ResourceTagSystem = "http://terminology.hl7.org.au/CodeSystem/resource-tag";
    private const string GroupTaskTag = "fulfilment-task-group";
    private const string DiagnosticRequestTaskTag = "fulfilment-task";

    private readonly FhirClient _client;
    private readonly FhirOptions _options;
    private readonly ILogger<FhirOrderService> _logger;

    public FhirOrderService(FhirClient client, IOptions<FhirOptions> options, ILogger<FhirOrderService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<FhirOrder> GetOrdersAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var searchParameters = new SearchParams()
            .LimitTo(_options.PageSize)
            .Where($"_tag={ResourceTagSystem}|{GroupTaskTag}")
            .Include("Task:patient")
            .Include("Task:requester")
            .Include("Task:owner")
            .Include("Task:focus", IncludeModifier.Iterate)
            .Include("ServiceRequest:encounter", IncludeModifier.Iterate)
            .Include("ServiceRequest:specimen", IncludeModifier.Iterate)
            .Include("PractitionerRole:practitioner", IncludeModifier.Iterate)
            .Include("PractitionerRole:organization", IncludeModifier.Iterate);

        // Diagnostic request Tasks point at the group Task via Task.partOf.
        searchParameters.RevInclude.Add(("Task:part-of", IncludeModifier.None));

        foreach (var parameter in _options.SearchParameters)
        {
            if (!string.IsNullOrWhiteSpace(parameter))
            {
                searchParameters.Where(parameter);
            }
        }

        _logger.LogInformation("Querying group Task resources from {BaseUrl}", _options.BaseUrl);

        // Resources are cached across pages so that included resources are only resolved once.
        var resources = new Dictionary<string, Resource?>(StringComparer.Ordinal);

        var bundle = await _client.SearchAsync<Task>(searchParameters, cancellationToken);

        while (bundle is not null)
        {
            var groupTasks = new List<Task>();

            foreach (var entry in bundle.Entry)
            {
                if (entry.Resource is null)
                {
                    continue;
                }

                resources[Key(entry.Resource)] = entry.Resource;

                if (entry.Search?.Mode == Bundle.SearchEntryMode.Match &&
                    entry.Resource is Task task &&
                    HasTag(task, GroupTaskTag))
                {
                    groupTasks.Add(task);
                }
            }

            foreach (var groupTask in groupTasks)
            {
                await foreach (var order in BuildOrdersAsync(groupTask, resources, cancellationToken))
                {
                    yield return order;
                }
            }

            if (bundle.NextLink is null)
            {
                yield break;
            }

            bundle = await _client.ContinueAsync(bundle, PageDirection.Next, cancellationToken);
        }
    }

    private async IAsyncEnumerable<FhirOrder> BuildOrdersAsync(
        Task groupTask,
        Dictionary<string, Resource?> resources,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var groupTaskReference = Key(groupTask);

        var diagnosticRequestTasks = resources.Values
            .OfType<Task>()
            .Where(task => HasTag(task, DiagnosticRequestTaskTag) &&
                           task.PartOf.Any(x => NormaliseReference(x.Reference) == groupTaskReference))
            .ToList();

        if (diagnosticRequestTasks.Count == 0)
        {
            _logger.LogWarning("Group Task {TaskId} has no diagnostic request Tasks", groupTask.Id);
            yield break;
        }

        foreach (var diagnosticRequestTask in diagnosticRequestTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ResolveAsync(diagnosticRequestTask.Focus, resources, cancellationToken)
                is not ServiceRequest serviceRequest)
            {
                _logger.LogWarning(
                    "Diagnostic request Task {TaskId} does not focus on a ServiceRequest",
                    diagnosticRequestTask.Id);
                continue;
            }

            var orderBundle = await BuildBundleAsync(
                [groupTask, diagnosticRequestTask, serviceRequest],
                resources,
                cancellationToken);

            yield return new FhirOrder(serviceRequest, diagnosticRequestTask, groupTask, orderBundle);
        }
    }

    /// <summary>
    /// Builds a collection Bundle containing the given resources and everything they
    /// (transitively) reference that is relevant to the order.
    /// </summary>
    private async Task<Bundle> BuildBundleAsync(
        IEnumerable<Resource> roots,
        Dictionary<string, Resource?> resources,
        CancellationToken cancellationToken)
    {
        var included = new Dictionary<string, Resource>(StringComparer.Ordinal);
        var pending = new Queue<Resource>(roots);

        while (pending.TryDequeue(out var resource))
        {
            if (!included.TryAdd(Key(resource), resource))
            {
                continue;
            }

            foreach (var reference in GetRelevantReferences(resource))
            {
                var referenced = await ResolveAsync(reference, resources, cancellationToken);

                if (referenced is not null)
                {
                    pending.Enqueue(referenced);
                }
            }
        }

        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Timestamp = DateTimeOffset.UtcNow
        };

        foreach (var resource in included.Values)
        {
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = $"{_options.BaseUrl.TrimEnd('/')}/{Key(resource)}",
                Resource = resource
            });
        }

        return bundle;
    }

    private static IEnumerable<ResourceReference?> GetRelevantReferences(Resource resource)
    {
        return resource switch
        {
            Task task => [task.Focus, task.For, task.Requester, task.Owner, .. task.PartOf],
            ServiceRequest serviceRequest =>
            [
                serviceRequest.Subject,
                serviceRequest.Encounter,
                serviceRequest.Requester,
                .. serviceRequest.Performer,
                .. serviceRequest.Insurance,
                .. serviceRequest.SupportingInfo,
                .. serviceRequest.ReasonReference,
                .. serviceRequest.Specimen
            ],
            PractitionerRole practitionerRole => [practitionerRole.Practitioner, practitionerRole.Organization],
            Coverage coverage => [coverage.Beneficiary],
            _ => []
        };
    }

    private async Task<Resource?> ResolveAsync(
        ResourceReference? reference,
        Dictionary<string, Resource?> resources,
        CancellationToken cancellationToken)
    {
        var key = NormaliseReference(reference?.Reference);

        if (key is null)
        {
            return null;
        }

        if (resources.TryGetValue(key, out var resource))
        {
            return resource;
        }

        try
        {
            resource = await _client.ReadAsync<Resource>(key, ct: cancellationToken);
        }
        catch (FhirOperationException exception)
        {
            _logger.LogWarning("Unable to resolve reference {Reference}: {Status}", key, exception.Status);
        }

        // Unresolvable references are cached as null so they are only requested once per import.
        resources[key] = resource;

        return resource;
    }

    /// <summary>
    /// Reduces a literal reference to "Type/id", dropping any base URL and version.
    /// Contained and logical references are not resolvable and return null.
    /// </summary>
    private static string? NormaliseReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            reference.StartsWith('#') ||
            reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var historyIndex = Array.IndexOf(segments, "_history");

        if (historyIndex >= 0)
        {
            segments = segments[..historyIndex];
        }

        if (segments.Length < 2 || !ModelInfo.IsKnownResource(segments[^2]))
        {
            return null;
        }

        return $"{segments[^2]}/{segments[^1]}";
    }

    private static string Key(Resource resource) => $"{resource.TypeName}/{resource.Id}";

    private static bool HasTag(Resource resource, string code) =>
        resource.Meta?.Tag.Any(x => x.System == ResourceTagSystem && x.Code == code) == true;
}
