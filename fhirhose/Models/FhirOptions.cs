namespace PathologyOrders.Configuration;

public sealed class FhirOptions
{
    public const string SectionName = "Fhir";

    public string BaseUrl { get; set; } = string.Empty;

    public int PageSize { get; set; } = 100;

    public List<string> SearchParameters { get; set; } = [];
}