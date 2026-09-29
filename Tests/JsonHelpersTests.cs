using System.Text.Json;
using distanceExport.Services;

namespace distanceExport.Tests;

public class JsonHelpersTests
{
    [Fact]
    public void ToCsvValue_String_ReturnsRawString()
    {
        var el = Parse("\"hello\"");
        Assert.Equal("hello", el.ToCsvValue());
    }

    [Fact]
    public void ToCsvValue_Null_ReturnsEmptyString()
    {
        var el = Parse("null");
        Assert.Equal("", el.ToCsvValue());
    }

    [Fact]
    public void ToCsvValue_Object_ReturnsRawJson()
    {
        var el = Parse("{\"a\":1}");
        Assert.Equal("{\"a\":1}", el.ToCsvValue());
    }

    [Fact]
    public void ToCsvValue_Number_ReturnsStringRepresentation()
    {
        var el = Parse("42");
        Assert.Equal("42", el.ToCsvValue());
    }

    [Fact]
    public void GetStringOrNull_MissingProperty_ReturnsNull()
    {
        var el = Parse("{\"a\":\"b\"}");
        Assert.Null(el.GetStringOrNull("missing"));
    }

    [Fact]
    public void GetStringOrNull_ExistingProperty_ReturnsValue()
    {
        var el = Parse("{\"a\":\"b\"}");
        Assert.Equal("b", el.GetStringOrNull("a"));
    }

    [Fact]
    public void GetStringOrNull_NonObject_ReturnsNull()
    {
        var el = Parse("\"just a string\"");
        Assert.Null(el.GetStringOrNull("a"));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
