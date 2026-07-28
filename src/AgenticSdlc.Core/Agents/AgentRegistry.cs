using AgenticSdlc.Core.Domain;

namespace AgenticSdlc.Core.Agents;

/// <summary>
/// Resolves an <see cref="IAgent"/> by <see cref="AgentType"/> from those registered in DI. The
/// system roles <see cref="AgentType.Join"/> and <see cref="AgentType.Packaging"/> are handled by the
/// engine directly and are not expected here.
/// </summary>
public sealed class AgentRegistry
{
    private readonly IReadOnlyDictionary<AgentType, IAgent> _agents;

    public AgentRegistry(IEnumerable<IAgent> agents)
    {
        _agents = agents.ToDictionary(a => a.Type);
    }

    public bool TryResolve(AgentType type, out IAgent agent) => _agents.TryGetValue(type, out agent!);

    public IAgent Resolve(AgentType type) =>
        _agents.TryGetValue(type, out var agent)
            ? agent
            : throw new InvalidOperationException($"No agent registered for type {type}.");

    public bool Has(AgentType type) => _agents.ContainsKey(type);
}
