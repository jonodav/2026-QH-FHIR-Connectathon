using System.Threading.Channels;
using fhirhose.HL7;
using fhirhose.Services;
using Hl7.Fhir.Rest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PathologyOrders.Configuration;
using PathologyOrders.Data;
using PathologyOrders.Services;

// Load appsettings.json from the build output so the app runs from any working directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services
    .AddOptions<FhirOptions>()
    .Bind(builder.Configuration.GetSection(FhirOptions.SectionName))
    .Validate(
        options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _),
        "FHIR BaseUrl must be a valid absolute URL")
    .Validate(
        options => options.PageSize is > 0 and <= 1000,
        "FHIR PageSize must be between 1 and 1000")
    .ValidateOnStart();

builder.Services
    .AddOptions<Hl7Options>().Bind(builder.Configuration.GetSection(Hl7Options.SectionName));

var connectionString =
    builder.Configuration.GetConnectionString("OrdersDatabase")
    ?? throw new InvalidOperationException(
        "Connection string 'OrdersDatabase' is missing.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<FhirOptions>>()
        .Value;

    return new FhirClient(
        options.BaseUrl,
        new FhirClientSettings
        {
            PreferredFormat = ResourceFormat.Json,
            VerifyFhirVersion = true,
            ReturnPreference = ReturnPreference.Minimal
        });
});

builder.Services.AddScoped<FhirOrderService, FhirOrderService>();
builder.Services.AddScoped<OrderImportService>();
builder.Services.AddScoped<OrderTranslationService>();

builder.Services.AddHostedService<OrderImportWorker>();
builder.Services.AddHostedService<OrderTranslationWorker>();

builder.Services.AddSingleton(
    Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        }));

builder.Services.AddScoped<IHl7OutboundQueue, Hl7OutboundQueue>();
builder.Services.AddScoped<Hl7MllpClient, Hl7MllpClient>();

builder.Services.AddHostedService<OrderSendingWorker>();

var host = builder.Build();

await CreateDatabaseAsync(host.Services);

await host.RunAsync();

static async Task CreateDatabaseAsync(IServiceProvider serviceProvider)
{
    await using var scope = serviceProvider.CreateAsyncScope();

    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await dbContext.Database.EnsureCreatedAsync();
}