using System.Globalization;
using System.Text.Json;

namespace distanceExport.Services;

/// <summary>
/// Identifies test/demo records that should never reach an export.
/// Two independent signals: a first/last name or email address containing "test", and (for
/// application-scoped exports) a <c>user-applications-registered-at</c> value that predates go-live.
/// </summary>
public static class TestRecordFilter
{
    /// <summary>Number of days to look back; defaults to 7, set from user input at startup.</summary>
    public static int LookbackDays { get; set; } = 7;

    /// <summary>Applications registered more than <see cref="LookbackDays"/> days ago are excluded as stale test data.</summary>
    public static DateTime RegisteredAtCutoff => DateTime.UtcNow.Date.AddDays(-LookbackDays);

    /// <summary>The export column carrying the registration timestamp; application-scoped.</summary>
    public static readonly ExportColumn RegisteredAtColumn =
        new("RegisteredAt", "user-applications-registered-at", ApplicationScoped: true);

    /// <summary>True when either name contains "test" (case-insensitive substring).</summary>
    public static bool IsTestName(string? firstName, string? lastName) =>
        ContainsTest(firstName) || ContainsTest(lastName);

    /// <summary>True when the email's local part contains "test" (case-insensitive substring).</summary>
    /// <remarks>Only the part before "@" is checked, so a domain such as "protest.org" is not a match.</remarks>
    public static bool IsTestEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var at = email.IndexOf('@');
        return ContainsTest(at < 0 ? email : email[..at]);
    }

    /// <summary>True when any of the name or email signals look like a test record.</summary>
    public static bool IsTestIdentity(string? firstName, string? lastName, string? email) =>
        IsTestName(firstName, lastName) || IsTestEmail(email);

    private static bool ContainsTest(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the registered-at value parses to a date before <see cref="RegisteredAtCutoff"/>.
    /// A missing or unparseable value is not treated as a test record.
    /// </summary>
    public static bool IsTestRegistration(string? registeredAt) =>
        TryParseRegisteredAt(registeredAt) is DateTime registeredAtDate && registeredAtDate < RegisteredAtCutoff;

    /// <summary>Reads the registered-at export value, which may be an ISO string or Mongo extended JSON.</summary>
    public static string? ReadRegisteredAt(JsonElement? exported)
    {
        if (exported is null || exported.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!exported.Value.TryGetProperty(RegisteredAtColumn.Field, out var el))
            return null;

        return el.ValueKind == JsonValueKind.Object ? el.GetRawText() : el.ToCsvValue();
    }

    private static DateTime? TryParseRegisteredAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        // Mongo extended JSON: {"$date":{"$numberLong":"1735689600000"}} or {"$date":"2026-01-01T..."}
        if (raw.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("$date", out var dateEl))
                {
                    if (dateEl.ValueKind == JsonValueKind.String)
                        return TryParseRegisteredAt(dateEl.GetString());

                    if (dateEl.ValueKind == JsonValueKind.Object &&
                        dateEl.TryGetProperty("$numberLong", out var numEl) &&
                        long.TryParse(numEl.ToCsvValue(), out var ms))
                    {
                        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }
}
