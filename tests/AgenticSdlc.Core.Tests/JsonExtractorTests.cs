using AgenticSdlc.Core.Llm;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>WP-2 verification: the extractor recovers JSON from the ways models actually wrap it.</summary>
public class JsonExtractorTests
{
    private record Sample(string Name, int Count, string[] Tags);

    [Fact]
    public void Parses_plain_json()
    {
        var (ok, value, _) = JsonExtractor.TryParse<Sample>("""{"name":"x","count":2,"tags":["a","b"]}""");
        Assert.True(ok);
        Assert.Equal("x", value!.Name);
        Assert.Equal(2, value.Count);
        Assert.Equal(2, value.Tags.Length);
    }

    [Fact]
    public void Strips_code_fences()
    {
        var raw = "```json\n{\"name\":\"y\",\"count\":1,\"tags\":[]}\n```";
        var (ok, value, _) = JsonExtractor.TryParse<Sample>(raw);
        Assert.True(ok);
        Assert.Equal("y", value!.Name);
    }

    [Fact]
    public void Ignores_surrounding_prose()
    {
        var raw = "Sure! Here is the result:\n{\"name\":\"z\",\"count\":3,\"tags\":[\"t\"]}\nLet me know if you need changes.";
        var (ok, value, _) = JsonExtractor.TryParse<Sample>(raw);
        Assert.True(ok);
        Assert.Equal("z", value!.Name);
        Assert.Equal(3, value.Count);
    }

    [Fact]
    public void Tolerates_trailing_commas()
    {
        var (ok, value, _) = JsonExtractor.TryParse<Sample>("""{"name":"t","count":1,"tags":["a",],}""");
        Assert.True(ok);
        Assert.Equal("t", value!.Name);
    }

    [Fact]
    public void Handles_braces_inside_strings()
    {
        var (ok, value, _) = JsonExtractor.TryParse<Sample>("""{"name":"a{b}c","count":1,"tags":[]}""");
        Assert.True(ok);
        Assert.Equal("a{b}c", value!.Name);
    }

    [Fact]
    public void Fails_on_unbalanced_json()
    {
        var (ok, _, error) = JsonExtractor.TryParse<Sample>("""{"name":"x","count":1""");
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here at all")]
    public void Fails_on_missing_json(string raw)
    {
        var (ok, _, error) = JsonExtractor.TryParse<Sample>(raw);
        Assert.False(ok);
        Assert.NotNull(error);
    }
}
