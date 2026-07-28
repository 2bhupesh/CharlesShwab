using AgenticSdlc.Core.Domain;
using AgenticSdlc.Core.Llm;
using AgenticSdlc.Core.Llm.Mock;
using Xunit;

namespace AgenticSdlc.Core.Tests;

/// <summary>
/// WP-2 verification: the mock catalog embeds responses, dispatches by (scenario, agent), and is
/// deterministic — the property the whole offline demo (NFR-5) rests on.
/// </summary>
public class MockLlmProviderTests
{
    private static LlmRequest RequestFor(string scenario, string agent) => new(
        SystemPrompt: "system",
        Messages: new[] { new LlmMessage("user", "do the thing") },
        Model: "claude-sonnet-5",
        MaxTokens: 4000,
        Metadata: new Dictionary<string, string> { ["scenario"] = scenario, ["agent"] = agent });

    [Fact]
    public void Catalog_loads_embedded_responses()
    {
        var catalog = new MockResponseCatalog();
        Assert.True(catalog.Count >= 2, $"expected embedded responses, found {catalog.Count}");
        Assert.NotNull(catalog.Resolve("greenfield", "RequirementIntelligence"));
        Assert.NotNull(catalog.Resolve("greenfield", "Planning"));
    }

    [Fact]
    public async Task Dispatches_by_agent()
    {
        var provider = new MockLlmProvider(new MockResponseCatalog());
        var spec = await provider.CompleteAsync(RequestFor("greenfield", "RequirementIntelligence"), default);
        var plan = await provider.CompleteAsync(RequestFor("greenfield", "Planning"), default);

        Assert.Contains("FR-1", spec.Text);
        Assert.Contains("milestones", plan.Text);
        Assert.NotEqual(spec.Text, plan.Text);
    }

    [Fact]
    public async Task Is_deterministic()
    {
        var provider = new MockLlmProvider(new MockResponseCatalog());
        var a = await provider.CompleteAsync(RequestFor("greenfield", "Planning"), default);
        var b = await provider.CompleteAsync(RequestFor("greenfield", "Planning"), default);

        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.InputTokens, b.InputTokens);
        Assert.Equal(a.OutputTokens, b.OutputTokens);
    }

    [Fact]
    public async Task Mock_output_parses_into_structured_shape()
    {
        var provider = new MockLlmProvider(new MockResponseCatalog());
        var spec = await provider.CompleteAsync(RequestFor("greenfield", "RequirementIntelligence"), default);

        var (ok, value, error) = JsonExtractor.TryParse<SpecShape>(spec.Text);
        Assert.True(ok, error);
        Assert.NotEmpty(value!.FunctionalRequirements);
        Assert.Equal("FR-1", value.FunctionalRequirements[0].Id);
    }

    [Fact]
    public async Task Throws_on_unknown_scenario_agent()
    {
        var provider = new MockLlmProvider(new MockResponseCatalog());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CompleteAsync(RequestFor("nonexistent", "Nope"), default));
    }

    [Fact]
    public void Selector_defaults_to_mock_without_api_key()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var selector = new LlmProviderSelector(
                new CoreOptions(),
                new AnthropicLlmProvider(),
                new MockLlmProvider(new MockResponseCatalog()));
            Assert.Equal(LlmProviderKind.Mock, selector.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }

    // Minimal shape mirroring the WP-3 SpecOutput contract, used to prove the canned JSON parses.
    private record SpecShape(List<ReqItem> FunctionalRequirements);
    private record ReqItem(string Id, string Title, string Description);
}
