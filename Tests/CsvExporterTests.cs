using Microsoft.Extensions.Logging.Abstractions;
using distanceExport.Services;

namespace distanceExport.Tests;

public class CsvExporterTests
{
    private readonly CsvExporter _exporter = new(NullLogger<CsvExporter>.Instance);

    [Fact]
    public async Task WriteAsync_NoRecords_ReturnsZeroAndSkipsFile()
    {
        var path = TempPath();

        var written = await _exporter.WriteAsync(Empty(), path);

        Assert.Equal(0, written);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteAsync_UnionsColumnsInFirstSeenOrder()
    {
        var path = TempPath();
        try
        {
            var rows = new[]
            {
                new Dictionary<string, string?> { ["A"] = "1", ["B"] = "2" },
                new Dictionary<string, string?> { ["B"] = "3", ["C"] = "4" },
            };

            var written = await _exporter.WriteAsync(ToAsync(rows), path);

            Assert.Equal(2, written);
            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("A,B,C", lines[0]);
            Assert.Equal("1,2,", lines[1]);
            Assert.Equal(",3,4", lines[2]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WriteAsync_EscapesCommasQuotesAndNewlines()
    {
        var path = TempPath();
        try
        {
            var rows = new[]
            {
                new Dictionary<string, string?> { ["Name"] = "Doe, John \"Q\"\nSuffix" },
            };

            await _exporter.WriteAsync(ToAsync(rows), path);

            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("\"Doe, John \"\"Q\"\"\nSuffix\"", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"csvexporter_test_{Guid.NewGuid():N}.csv");

    private static async IAsyncEnumerable<Dictionary<string, string?>> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<Dictionary<string, string?>> ToAsync(IEnumerable<Dictionary<string, string?>> rows)
    {
        await Task.CompletedTask;
        foreach (var row in rows)
            yield return row;
    }
}
