using System.Text.Json;
using distanceExport.Services;

namespace distanceExport.Tests;

public class TestRecordFilterTests
{
    [Theory]
    [InlineData("Test", "Smith")]
    [InlineData("Jane", "TESTER")]
    [InlineData("contest", "Smith")] // substring match is intentional
    public void IsTestName_MatchesTestSubstring(string first, string last)
    {
        Assert.True(TestRecordFilter.IsTestName(first, last));
    }

    [Theory]
    [InlineData("Jane", "Smith")]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    public void IsTestName_LeavesRealNamesAlone(string? first, string? last)
    {
        Assert.False(TestRecordFilter.IsTestName(first, last));
    }

    [Theory]
    [InlineData("test@lsua.edu")]
    [InlineData("Jane.Test@gmail.com")]
    [InlineData("TESTER99@gmail.com")]
    [InlineData("test")] // no "@" at all
    public void IsTestEmail_MatchesTestInLocalPart(string email)
    {
        Assert.True(TestRecordFilter.IsTestEmail(email));
    }

    [Theory]
    [InlineData("jane.smith@gmail.com")]
    [InlineData("jane@protest.org")] // domain is not checked
    [InlineData("jane@test.com")]    // domain is not checked
    [InlineData(null)]
    [InlineData("")]
    public void IsTestEmail_LeavesRealEmailsAlone(string? email)
    {
        Assert.False(TestRecordFilter.IsTestEmail(email));
    }

    [Fact]
    public void IsTestIdentity_CombinesNameAndEmailSignals()
    {
        Assert.True(TestRecordFilter.IsTestIdentity("Test", "Smith", "jane@gmail.com"));
        Assert.True(TestRecordFilter.IsTestIdentity("Jane", "Smith", "testuser@gmail.com"));
        Assert.False(TestRecordFilter.IsTestIdentity("Jane", "Smith", "jane@gmail.com"));
        Assert.False(TestRecordFilter.IsTestIdentity(null, null, null));
    }

    [Fact]
    public void IsTestRegistration_BeforeCutoff_IsTest()
    {
        var beforeCutoff = TestRecordFilter.RegisteredAtCutoff.AddMinutes(-1);
        var wellBeforeCutoff = TestRecordFilter.RegisteredAtCutoff.AddDays(-30);

        Assert.True(TestRecordFilter.IsTestRegistration(beforeCutoff.ToString("O")));
        Assert.True(TestRecordFilter.IsTestRegistration(wellBeforeCutoff.ToString("O")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void IsTestRegistration_AtOrAfterCutoffOrUnknown_IsNotTest(string? raw)
    {
        Assert.False(TestRecordFilter.IsTestRegistration(raw));
    }

    [Fact]
    public void IsTestRegistration_AtCutoff_IsNotTest()
    {
        Assert.False(TestRecordFilter.IsTestRegistration(TestRecordFilter.RegisteredAtCutoff.ToString("O")));
        Assert.False(TestRecordFilter.IsTestRegistration(DateTime.UtcNow.AddDays(-1).ToString("O")));
    }

    [Fact]
    public void IsTestRegistration_ParsesMongoExtendedJson()
    {
        var before = new DateTimeOffset(TestRecordFilter.RegisteredAtCutoff.AddDays(-1)).ToUnixTimeMilliseconds();
        var after = new DateTimeOffset(TestRecordFilter.RegisteredAtCutoff.AddDays(1)).ToUnixTimeMilliseconds();

        Assert.True(TestRecordFilter.IsTestRegistration($"{{\"$date\":{{\"$numberLong\":\"{before}\"}}}}"));
        Assert.False(TestRecordFilter.IsTestRegistration($"{{\"$date\":{{\"$numberLong\":\"{after}\"}}}}"));
    }

    [Fact]
    public void ReadRegisteredAt_ReadsStringAndObjectShapes()
    {
        var stringShape = Parse("{\"RegisteredAt\":\"2026-01-01T00:00:00Z\"}");
        Assert.Equal("2026-01-01T00:00:00Z", TestRecordFilter.ReadRegisteredAt(stringShape));

        var objectShape = Parse("{\"RegisteredAt\":{\"$date\":\"2026-01-01T00:00:00Z\"}}");
        Assert.Equal("{\"$date\":\"2026-01-01T00:00:00Z\"}", TestRecordFilter.ReadRegisteredAt(objectShape));

        Assert.Null(TestRecordFilter.ReadRegisteredAt(Parse("{}")));
        Assert.Null(TestRecordFilter.ReadRegisteredAt(null));
    }

    [Fact]
    public void RegisteredAtColumn_IsApplicationScoped()
    {
        Assert.Equal("user-applications-registered-at", TestRecordFilter.RegisteredAtColumn.Slug);
        Assert.True(TestRecordFilter.RegisteredAtColumn.ApplicationScoped);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
