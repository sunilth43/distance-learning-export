using System.Text;
using Microsoft.Extensions.Logging;

namespace distanceExport.Services;

public sealed class CsvExporter
{
    private readonly ILogger<CsvExporter> _logger;

    public CsvExporter(ILogger<CsvExporter> logger)
    {
        _logger = logger;
    }

    /// <summary>Writes records to CSV. Columns are the union of all keys seen, in first-seen order.</summary>
    public async Task<int> WriteAsync(
        IAsyncEnumerable<Dictionary<string, string?>> records,
        string path,
        CancellationToken ct = default)
    {
        var rows = new List<Dictionary<string, string?>>();
        var columns = new List<string>();
        var seen = new HashSet<string>();

        await foreach (var row in records.WithCancellation(ct))
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                    columns.Add(key);
            }
            rows.Add(row);
        }

        if (rows.Count == 0)
        {
            _logger.LogWarning("No records to write; skipping CSV creation.");
            return 0;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        try
        {
            await using var writer = new StreamWriter(path, append: false, Encoding.UTF8);

            await writer.WriteLineAsync(string.Join(",", columns.Select(CsvEscape)));

            foreach (var row in rows)
            {
                var values = columns.Select(col =>
                    row.TryGetValue(col, out var val) ? val ?? "" : "");
                await writer.WriteLineAsync(string.Join(",", values.Select(CsvEscape)));
            }
        }
        catch (IOException ex)
        {
            throw new SegmentApiException($"Failed to write CSV to '{path}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SegmentApiException($"Permission denied writing CSV to '{path}': {ex.Message}", ex);
        }

        return rows.Count;
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
