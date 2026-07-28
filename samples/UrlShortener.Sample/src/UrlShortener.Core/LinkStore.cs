using System.Collections.Concurrent;

namespace UrlShortener.Core;

/// <summary>A shortened link: its code, the original target, and when it was created.</summary>
public sealed record Link(string Code, string TargetUrl, DateTimeOffset CreatedAt);

/// <summary>Storage abstraction so the in-memory store can be replaced by a database later.</summary>
public interface ILinkStore
{
    Link Create(string targetUrl);
    Link? Get(string code);
}

/// <summary>In-memory link store with collision-checked code generation.</summary>
public sealed class InMemoryLinkStore : ILinkStore
{
    private readonly ConcurrentDictionary<string, Link> _links = new();

    public Link Create(string targetUrl)
    {
        while (true)
        {
            var code = ShortCode.Generate();
            var link = new Link(code, targetUrl, DateTimeOffset.UtcNow);
            if (_links.TryAdd(code, link))
                return link;
        }
    }

    public Link? Get(string code) => _links.TryGetValue(code, out var link) ? link : null;
}

/// <summary>Generates URL-safe short codes.</summary>
public static class ShortCode
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string Generate(int length = 7)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        return new string(chars);
    }
}

/// <summary>Validates that a target URL is an absolute http/https URL.</summary>
public static class UrlValidator
{
    public static bool IsValid(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
