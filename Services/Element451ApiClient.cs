using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using distanceExport.Options;

namespace distanceExport.Services;

/// <summary>One column requested from the bulk user-export endpoint (see <see cref="Element451ApiClient.ExportUserFieldsAsync"/>).</summary>
public sealed record ExportColumn(string Field, string Slug, bool ApplicationScoped = false);

public sealed class Element451ApiClient
{
    private static readonly JsonSerializerOptions ExportRequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record ExportRequestBody([property: JsonPropertyName("item")] ExportRequestItem Item);

    private sealed record ExportRequestItem(
        [property: JsonPropertyName("options")] ExportRequestOptions Options,
        [property: JsonPropertyName("template")] ExportRequestTemplate Template,
        [property: JsonPropertyName("users")] string[] Users);

    private sealed record ExportRequestOptions([property: JsonPropertyName("column_key")] string ColumnKey);

    private sealed record ExportRequestTemplate([property: JsonPropertyName("columns")] ExportRequestColumn[] Columns);

    private sealed record ExportRequestColumn(
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("scope")] ExportRequestScope? Scope = null);

    private sealed record ExportRequestScope([property: JsonPropertyName("application")] string Application);

    private readonly HttpClient _http;
    private readonly ApiOptions _options;
    private readonly ILogger<Element451ApiClient> _logger;

    public Element451ApiClient(HttpClient http, IOptions<ApiOptions> options, ILogger<Element451ApiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        _http.BaseAddress = new Uri(_options.BaseUrl);
        // Must exceed the retry policy's worst case (4 attempts x 30s + 2s+4s+8s backoff = 134s),
        // otherwise HttpClient's own timeout can abort the operation mid-retry.
        _http.Timeout = TimeSpan.FromSeconds(150);
        _http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(_options.AuthorizationToken);
        _http.DefaultRequestHeaders.Add("Feature", _options.FeatureToken);
    }

    /// <summary>
    /// Fetches users matching an ad-hoc analytics filter (e.g. "formSubmitted"), paging through
    /// v2/users/segments/preview. The filter is supplied directly in the POST body.
    /// NOTE: this endpoint's "offset" is a page number, not a row-skip count
    /// (e.g. limit=100&amp;offset=1 returns records 101.. rather than skipping 1 row).
    /// We page starting at offset=0, incrementing by 1, until a page comes back empty.
    /// </summary>
    public async IAsyncEnumerable<Dictionary<string, JsonElement>> GetUsersByAnalyticsFilterAsync(
        object analyticsFilter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var offset = 0;
        var totalCount = 0;

        var bodyJson = JsonSerializer.Serialize(new { item = analyticsFilter });

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"v2/users/segments/preview" +
                      $"?analytics={_options.AnalyticsToken}" +
                      $"&limit={_options.Limit}&offset={offset}&sort=-_id&v=3";

            _logger.LogInformation("Analytics filter preview: requesting page {Offset} (up to {Limit} records)", offset, _options.Limit);

            using var body = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            var response = await SendAsync(HttpMethod.Post, url, body, ct);
            var items = await ExtractDataArrayAsync(response, url, ct); // ExtractDataArrayAsync disposes the response

            if (items.Count == 0)
                break;

            foreach (var item in items)
                yield return ToDictionary(item);

            totalCount += items.Count;
            offset++;
        }

        _logger.LogInformation("Analytics filter preview: fetched {Total} records across {Pages} page(s)", totalCount, offset);
    }

    /// <summary>Gets the first profile record for a user, or null if the API returned no data.</summary>
    public async Task<JsonElement?> GetUserProfileAsync(string userId, CancellationToken ct = default)
    {
        var url = $"v2/users/{userId}/profile/general?analytics={_options.AnalyticsToken}";

        var response = await SendAsync(HttpMethod.Get, url, body: null, ct);
        var root = await ParseJsonAsync(response, ct); // ParseJsonAsync takes ownership and disposes the response

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new SegmentApiException($"Unrecognized profile response shape for user '{userId}'. Raw: {Truncate(root.GetRawText(), 500)}");

        foreach (var item in dataEl.EnumerateArray())
            return item.Clone();

        return null;
    }

    /// <summary>Gets the user's SCHOOL_ID external identifier, or null if none has been generated yet.</summary>
    public async Task<string?> GetSchoolIdAsync(string userId, CancellationToken ct = default)
    {
        var url = $"v2/users/{userId}/profile/identities?analytics={_options.AnalyticsToken}";

        var response = await SendAsync(HttpMethod.Get, url, body: null, ct);
        var root = await ParseJsonAsync(response, ct); // ParseJsonAsync takes ownership and disposes the response

        if (!root.TryGetProperty("data", out var dataEl) ||
            !dataEl.TryGetProperty("identities", out var identitiesEl) ||
            !identitiesEl.TryGetProperty("user_data", out var userDataEl) ||
            !userDataEl.TryGetProperty("user-identities-root", out var identitiesRootEl) ||
            identitiesRootEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var identity in identitiesRootEl.EnumerateArray())
        {
            if (string.Equals(identity.GetStringOrNull("type"), "SCHOOL_ID", StringComparison.OrdinalIgnoreCase))
                return identity.GetStringOrNull("value");
        }

        return null;
    }

    /// <summary>Gets checklist items for a decision.</summary>
    public async Task<List<JsonElement>> GetDecisionChecklistAsync(string decisionId, CancellationToken ct = default)
    {
        var url = $"v2/decisions/{decisionId}/checklist?analytics={_options.AnalyticsToken}";

        var items = await GetForDataArrayAsync(url, ct);
        return items;
    }

    /// <summary>Gets a major's details (e.g. its program code).</summary>
    public async Task<JsonElement> GetMajorAsync(string majorGuid, CancellationToken ct = default)
    {
        var url = $"v2/majors/{majorGuid}?analytics={_options.AnalyticsToken}";

        var response = await SendAsync(HttpMethod.Get, url, body: null, ct);
        var root = await ParseJsonAsync(response, ct); // ParseJsonAsync takes ownership and disposes the response

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
            throw new SegmentApiException($"Unrecognized major response shape for '{majorGuid}'. Raw: {Truncate(root.GetRawText(), 500)}");

        return dataEl.Clone();
    }

    /// <summary>Gets the guids of all terms currently marked <c>active: true</c>.</summary>
    public async Task<List<string>> GetActiveTermGuidsAsync(CancellationToken ct = default)
    {
        var url = $"v2/terms?analytics={_options.AnalyticsToken}";

        var items = await GetForDataArrayAsync(url, ct);

        return items
            .Where(t => t.TryGetProperty("active", out var activeEl) && activeEl.ValueKind == JsonValueKind.True)
            .Select(t => t.GetStringOrNull("guid"))
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .ToList();
    }

    /// <summary>
    /// Fetches slug-mapped field values for a single user via the bulk export endpoint, e.g.
    /// custom fields, identities, and citizenship data not present in /profile/general.
    /// Columns with an application scope (e.g. payment fields) require <paramref name="applicationGuid"/>.
    /// </summary>
    public async Task<JsonElement?> ExportUserFieldsAsync(
        string userId,
        IReadOnlyList<ExportColumn> columns,
        string? applicationGuid,
        CancellationToken ct = default)
    {
        if (columns.Any(c => c.ApplicationScoped) && string.IsNullOrEmpty(applicationGuid))
            throw new ArgumentException(
                "One or more columns require an application scope, but no applicationGuid was provided.",
                nameof(applicationGuid));

        var templateColumns = columns
            .Select(c => new ExportRequestColumn(
                c.Field,
                Mode: "slug",
                c.Slug,
                c.ApplicationScoped ? new ExportRequestScope(applicationGuid!) : null))
            .ToArray();

        var payload = new ExportRequestBody(
            new ExportRequestItem(
                new ExportRequestOptions(ColumnKey: "field"),
                new ExportRequestTemplate(templateColumns),
                Users: new[] { userId }));

        using var body = new StringContent(
            JsonSerializer.Serialize(payload, ExportRequestJsonOptions), Encoding.UTF8, "application/json");
        var response = await SendAsync(HttpMethod.Post, "v2/users/export", body, ct);
        var root = await ParseJsonAsync(response, ct); // ParseJsonAsync takes ownership and disposes the response

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new SegmentApiException($"Unrecognized export response shape for user '{userId}'. Raw: {Truncate(root.GetRawText(), 500)}");

        foreach (var item in dataEl.EnumerateArray())
            return item.Clone();

        return null;
    }

    private async Task<List<JsonElement>> GetForDataArrayAsync(string url, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, url, body: null, ct);
        return await ExtractDataArrayAsync(response, url, ct); // ExtractDataArrayAsync disposes the response
    }

    private async Task<List<JsonElement>> ExtractDataArrayAsync(HttpResponseMessage response, string url, CancellationToken ct)
    {
        var root = await ParseJsonAsync(response, ct);

        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
            items = root;
        else if (root.TryGetProperty("data", out var dataEl))
            items = dataEl;
        else if (root.TryGetProperty("results", out var resultsEl))
            items = resultsEl;
        else
            throw new SegmentApiException(
                $"Unrecognized response shape for '{url}' (no array, 'data', or 'results' property). Raw: {Truncate(root.GetRawText(), 500)}");

        var list = new List<JsonElement>();
        foreach (var item in items.EnumerateArray())
            list.Add(item.Clone());
        return list;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = body };
            response = await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SegmentApiException($"Request to '{url}' timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new SegmentApiException($"Network error calling '{url}': {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new SegmentApiException(
                $"API returned {(int)response.StatusCode} {response.StatusCode} for '{url}'. Body: {Truncate(errBody, 500)}");
        }

        return response;
    }

    private static async Task<JsonElement> ParseJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new SegmentApiException("API response was not valid JSON.", ex);
            }
        }
    }

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement obj)
    {
        var row = new Dictionary<string, JsonElement>();
        foreach (var prop in obj.EnumerateObject())
            row[prop.Name] = prop.Value.Clone();
        return row;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
