using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using distanceExport;
using distanceExport.Options;
using distanceExport.Services;

var builder = Host.CreateApplicationBuilder(args);

var assembly = Assembly.GetExecutingAssembly();
var resourceName = assembly.GetManifestResourceNames()
    .Single(name => name.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase));
using (var stream = assembly.GetManifestResourceStream(resourceName)!)
{
    builder.Configuration.AddJsonStream(stream);
}

builder.Services
    .AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName));
builder.Services
    .AddOptions<SegmentsOptions>()
    .Bind(builder.Configuration.GetSection(SegmentsOptions.SectionName));

builder.Services.AddHttpClient<Element451ApiClient>()
    .AddPolicyHandler(GetRetryPolicy());

builder.Services.AddSingleton<CsvExporter>();
builder.Services.AddSingleton<ChecklistRowBuilder>();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();

try
{
    var apiOptions = host.Services.GetRequiredService<IOptions<ApiOptions>>().Value;
    apiOptions.Validate();

    var segments = host.Services.GetRequiredService<IOptions<SegmentsOptions>>().Value;

    TestRecordFilter.LookbackDays = ReadLookbackDays(logger);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await RundistanceExportAsync(
        host,
        logger,
        segments.DistanceLearning,
        cts.Token);

    return 0;

}
catch (OperationCanceledException)
{
    logger.LogWarning("Cancelled by user.");
    return 130;
}
catch (SegmentApiException ex)
{
    logger.LogError("{Message}", ex.Message);
    return 1;
}
catch (InvalidOperationException ex)
{
    logger.LogError("Configuration error: {Message}", ex.Message);
    return 1;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Unexpected error.");
    return 1;
}

static async Task RundistanceExportAsync(IHost host, ILogger logger, DistanceLearningOptions segment, CancellationToken token)
{
    segment.Validate();

    var rowBuilder = host.Services.GetRequiredService<ChecklistRowBuilder>();
    var csvExporter = host.Services.GetRequiredService<CsvExporter>();
    var csvPath = WithTimestamp(segment.CsvPath);

    logger.LogInformation("Starting LSUA DistanceLearning export...");

    var written = await csvExporter.WriteAsync(
        rowBuilder.BuildRowsAsync(segment.ApplicationGuid, token),
        csvPath,
        token);

    logger.LogInformation("Wrote {Count} records to {Path}", written, Path.GetFullPath(csvPath));
}

/// <summary>Inserts a "yyyyMMdd_HHmmss" timestamp before the file extension, e.g. "LSUA_RFI_Elem451.csv" -&gt; "LSUA_RFI_Elem451_20260904_153000.csv".</summary>
static string WithTimestamp(string csvPath)
{
    var directory = Path.GetDirectoryName(csvPath);
    var fileName = Path.GetFileNameWithoutExtension(csvPath);
    var extension = Path.GetExtension(csvPath);
    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

    var timestampedFileName = $"{fileName}_{timestamp}{extension}";
    return string.IsNullOrEmpty(directory) ? timestampedFileName : Path.Combine(directory, timestampedFileName);
}

/// <summary>Prompts for how many days back to look; digits only, blank defaults to 7.</summary>
static int ReadLookbackDays(ILogger logger)
{
    Console.Write("Look back how many days? [7]: ");
    var input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input))
        return 7;

    input = input.Trim();
    if (!input.All(char.IsDigit) || !int.TryParse(input, out var days) || days <= 0)
    {
        logger.LogError("Invalid input '{Input}': expected a positive whole number of days.", input);
        throw new InvalidOperationException($"Invalid days value: '{input}'. Expected a positive whole number.");
    }

    return days;
}

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError() // 5xx and 408
        .OrResult(r => (int)r.StatusCode == 429) // rate limited
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
}
