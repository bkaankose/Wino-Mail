using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Ports;
using Wino.Agent.Runtime.Session;

namespace Wino.Agent.Runtime;

/// <summary>The runtime: one per app, wires the ports, owns the brain, opens sessions.</summary>
public interface IAgentRuntime
{
    /// <summary>One per assistant window; cheap.</summary>
    IAgentSession OpenSession(AgentSessionOptions? options = null);

    /// <summary>For the memory screen and settings.</summary>
    IAgentBrain Brain { get; }

    /// <summary>Loads the brain from storage. Call once at start; sessions work on an empty brain until then.</summary>
    Task InitializeAsync(CancellationToken ct);
}

public sealed class AgentRuntime : IAgentRuntime
{
    private readonly IReadingClient _reader;
    private readonly IAgentDomainQueries _queries;
    private readonly IAgentClock _clock;
    private readonly IAgentPrompter? _prompter;
    private readonly AgentBrain _brain;

    public AgentRuntime(IReadingClient reader, IAgentDomainQueries queries, IAgentBrainStorage storage, IAgentClock clock, IAgentPrompter? prompter = null, TimeProvider? time = null)
    {
        _reader = reader;
        _queries = queries;
        _clock = clock;
        _prompter = prompter;
        _brain = new AgentBrain(storage, time);
    }

    public IAgentBrain Brain => _brain;

    /// <summary>The brain with its queries (for the eval and the memory screen's grouping).</summary>
    public AgentBrain BrainInternal => _brain;

    public Task InitializeAsync(CancellationToken ct) => _brain.LoadAsync(ct);

    public IAgentSession OpenSession(AgentSessionOptions? options = null) => new AgentSession(_reader, _queries, _brain, _clock, _prompter, options);
}
