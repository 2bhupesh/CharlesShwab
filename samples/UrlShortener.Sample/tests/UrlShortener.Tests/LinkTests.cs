using UrlShortener.Core;
using Xunit;

namespace UrlShortener.Tests;

public class LinkTests
{
    [Fact]
    public void Create_stores_and_returns_a_resolvable_link()
    {
        var store = new InMemoryLinkStore();
        var link = store.Create("https://example.com/page");
        Assert.Equal("https://example.com/page", store.Get(link.Code)?.TargetUrl);
    }

    [Fact]
    public void Unknown_code_resolves_to_null()
    {
        Assert.Null(new InMemoryLinkStore().Get("missing"));
    }

    [Theory]
    [InlineData("https://a.com", true)]
    [InlineData("http://a.com", true)]
    [InlineData("ftp://a.com", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void Validates_absolute_http_urls(string url, bool expected)
    {
        Assert.Equal(expected, UrlValidator.IsValid(url));
    }

    [Fact]
    public void Codes_are_url_safe_and_sized()
    {
        var code = ShortCode.Generate();
        Assert.InRange(code.Length, 6, 8);
        Assert.Matches("^[A-Za-z0-9]+$", code);
    }

    [Fact]
    public void Codes_are_unique_across_links()
    {
        var store = new InMemoryLinkStore();
        var a = store.Create("https://a.com");
        var b = store.Create("https://b.com");
        Assert.NotEqual(a.Code, b.Code);
    }
}
