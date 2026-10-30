using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PathologyOrders.Data;
using PathologyOrders.Models;
using PathologyOrders.Services;
using Task = System.Threading.Tasks.Task;

namespace fhirhose.Services;

public sealed class OrderImportService
{
    private readonly AppDbContext _dbContext;
    private readonly FhirOrderService _fhirOrderService;
    private readonly ILogger<OrderImportService> _logger;
    private readonly FhirJsonSerializer _serializer = new();

    public OrderImportService(AppDbContext dbContext, FhirOrderService fhirOrderService, ILogger<OrderImportService> logger)
    {
        _dbContext = dbContext;
        _fhirOrderService = fhirOrderService;
        _logger = logger;
    }

    public async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        var processed = 0;
        var created = 0;
        var updated = 0;

        await foreach (var resource in _fhirOrderService.GetOrdersAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(resource.Id))
            {
                _logger.LogWarning("Skipping ServiceRequest without a resource ID");
                continue;
            }

            var pathOrder = await _dbContext.PathologyOrders
                .SingleOrDefaultAsync(
                    x => x.FhirId == resource.Id,
                    cancellationToken);

            if (pathOrder is null)
            {
                pathOrder = new PathologyOrder
                {
                    FhirId = resource.Id,
                    RawJson = string.Empty,
                    ImportedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    SourceHash = string.Empty
                };

                _dbContext.PathologyOrders.Add(pathOrder);
                created++;
            }
            else
            {
                updated++;
            }

            Map(resource, pathOrder);
            processed++;

            if (processed % 100 == 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Processed {Count} pathology orders",
                    processed);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Import complete. Processed: {Processed}, Created: {Created}, Updated: {Updated}",
            processed,
            created,
            updated);
    }

    private void Map(ServiceRequest source, PathologyOrder destination)
    {
        var identifier = source.Identifier.FirstOrDefault();
        var coding = source.Code?.Coding.FirstOrDefault();

        destination.IdentifierSystem = identifier?.System;
        destination.IdentifierValue = identifier?.Value;

        destination.Status = source.Status?.ToString();
        destination.Intent = source.Intent?.ToString();

        destination.OrderCode = coding?.Code;
        destination.OrderCodeSystem = coding?.System;
        destination.OrderDescription = source.Code?.Text ?? coding?.Display;

        destination.PatientReference = source.Subject?.Reference;
        destination.EncounterReference = source.Encounter?.Reference;
        destination.RequesterReference = source.Requester?.Reference;

        destination.RequisitionGroupNumber = source.Requisition?.Value;
        destination.RequisitionNumber = source.IdElement?.Value;

        destination.AuthoredOn = source.AuthoredOn;
        destination.FhirLastUpdated = source.Meta?.LastUpdated?.UtcDateTime;

        destination.RawJson = _serializer.SerializeToString(source);

        destination.UpdatedAtUtc = DateTime.UtcNow;
    }
}