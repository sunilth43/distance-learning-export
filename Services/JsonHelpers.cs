using System.Globalization;
using System.Text.Json;

namespace distanceExport.Services;

public static class JsonHelpers
{
    private static readonly TimeZoneInfo CentralTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

    public static string ToCsvValue(this JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Object or JsonValueKind.Array => el.GetRawText(),
        _ => el.ToString()
    };

    public static string? GetStringOrNull(this JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(propertyName, out var val)
            ? val.ToCsvValue()
            : null;

    /// <summary>Converts a UTC (or offset-bearing) API timestamp string to Central time, DST-aware (CST/CDT).</summary>
    public static string? ToCentralTimeString(string? utcTimestamp)
    {
        if (string.IsNullOrWhiteSpace(utcTimestamp))
            return utcTimestamp;

        if (!DateTime.TryParse(utcTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            return utcTimestamp;

        var central = TimeZoneInfo.ConvertTimeFromUtc(utc, CentralTimeZone);
        return central.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }
}
