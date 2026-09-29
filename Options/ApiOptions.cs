namespace distanceExport.Options;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public required string BaseUrl { get; init; }
    public required string AnalyticsToken { get; init; }
    public required string FeatureToken { get; init; }
    public required string AuthorizationToken { get; init; }
    public int Limit { get; init; } = 100;

    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(BaseUrl)) missing.Add(nameof(BaseUrl));
        if (string.IsNullOrWhiteSpace(AnalyticsToken)) missing.Add(nameof(AnalyticsToken));
        if (string.IsNullOrWhiteSpace(FeatureToken)) missing.Add(nameof(FeatureToken));
        if (string.IsNullOrWhiteSpace(AuthorizationToken)) missing.Add(nameof(AuthorizationToken));
        if (Limit <= 0) missing.Add(nameof(Limit));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Invalid configuration under \"Api\": {string.Join(", ", missing)}. Check appsettings.json.");
    }
}

public sealed class DistanceLearningOptions
{
    public required string ApplicationGuid { get; init; }
    public required string CsvPath { get; init; }

    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ApplicationGuid)) missing.Add(nameof(ApplicationGuid));
        if (string.IsNullOrWhiteSpace(CsvPath)) missing.Add(nameof(CsvPath));

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Invalid configuration under \"Segments:DistanceLearning\": {string.Join(", ", missing)}. Check appsettings.json.");
    }
}

public sealed class SegmentsOptions
{
    public const string SectionName = "Segments";

    public required DistanceLearningOptions DistanceLearning { get; init; }
}
