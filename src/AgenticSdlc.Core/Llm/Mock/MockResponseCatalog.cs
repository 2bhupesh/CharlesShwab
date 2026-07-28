using System.Collections.Concurrent;
using System.Reflection;

namespace AgenticSdlc.Core.Llm.Mock;

/// <summary>
/// Loads deterministic canned agent responses embedded as resources under
/// <c>Llm/Mock/Responses/{scenario}.{agent}.json</c>. Lookup tries the scenario-specific response
/// first, then a <c>default.{agent}</c> fallback. This is seed data (test fixtures), not platform
/// logic — scenario knowledge lives here, never in the engine (NFR-1).
/// </summary>
public sealed class MockResponseCatalog
{
    private const string Marker = ".Llm.Mock.Responses.";
    private readonly ConcurrentDictionary<string, string> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public MockResponseCatalog()
    {
        var asm = typeof(MockResponseCatalog).Assembly;
        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            var idx = resourceName.IndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0 || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var key = resourceName
                .Substring(idx + Marker.Length)
                .Replace(".json", "", StringComparison.OrdinalIgnoreCase);

            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            _byKey[key] = reader.ReadToEnd();
        }
    }

    /// <summary>Number of loaded canned responses (used by tests to assert embedding worked).</summary>
    public int Count => _byKey.Count;

    /// <summary>
    /// Resolves a canned response, most specific first: <c>{scenario}.{agent}.{variant}</c> (e.g. a
    /// per-node generation response), then <c>{scenario}.{agent}</c>, then <c>default.{agent}</c>.
    /// Returns null when none exists.
    /// </summary>
    public string? Resolve(string scenario, string agent, string? variant = null)
    {
        if (!string.IsNullOrEmpty(variant) && _byKey.TryGetValue($"{scenario}.{agent}.{variant}", out var perNode))
            return perNode;
        if (_byKey.TryGetValue($"{scenario}.{agent}", out var scoped))
            return scoped;
        if (_byKey.TryGetValue($"default.{agent}", out var fallback))
            return fallback;
        return null;
    }

    /// <summary>Resolves only the exact <c>{scenario}.{agent}.{variant}</c> key, with no fallback.</summary>
    public string? ResolveExact(string scenario, string agent, string variant) =>
        _byKey.TryGetValue($"{scenario}.{agent}.{variant}", out var v) ? v : null;
}
