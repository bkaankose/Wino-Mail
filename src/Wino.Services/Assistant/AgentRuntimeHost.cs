#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Agent.Runtime;
using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Ports;

namespace Wino.Services.Assistant;

/// <summary>
/// The one Wino Agent Runtime of the app with its brain sync: created with the app's ports, loaded once on first
/// use (the brain file), and the place the assistant control, the memory screen and the settings page meet.
/// </summary>
public interface IAgentRuntimeHost
{
    IAgentRuntime Runtime { get; }

    /// <summary>The brain with its queries (grouping on the memory screen).</summary>
    AgentBrain Brain { get; }

    BrainSync Sync { get; }

    /// <summary>The "Remember this?" relay: the assistant control sets <see cref="AgentPrompterRelay.Handler"/> while it is open.</summary>
    AgentPrompterRelay Prompter { get; }

    /// <summary>Loads the brain once; later calls return at once. Safe to call from anywhere.</summary>
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>App start or foreground: asks the server for the brain revision when sync is on.</summary>
    Task CheckSyncAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentRuntimeHost : IAgentRuntimeHost
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private Task? _initialized;

    public AgentRuntimeHost(IReadingClient reader, IAgentDomainQueries queries, IAgentBrainStorage storage, IAgentClock clock, IBrainSyncTransport transport, AgentPrompterRelay prompter)
    {
        Prompter = prompter;
        var runtime = new AgentRuntime(reader, queries, storage, clock, prompter);
        Runtime = runtime;
        Brain = runtime.BrainInternal;
        Sync = new BrainSync(Brain, storage, transport);
    }

    public IAgentRuntime Runtime { get; }

    public AgentBrain Brain { get; }

    public BrainSync Sync { get; }

    public AgentPrompterRelay Prompter { get; }

    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized is { } done) return done;
        return InitializeAsync(cancellationToken);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized is { } done)
            {
                await done.ConfigureAwait(false);
                return;
            }

            var task = Runtime.InitializeAsync(cancellationToken);
            await task.ConfigureAwait(false);
            _initialized = Task.CompletedTask;
        }
        finally { _initialization.Release(); }
    }

    public async Task CheckSyncAsync(CancellationToken cancellationToken = default)
    {
        if (!Sync.IsEnabled) return;
        try
        {
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            await Sync.CheckAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The brain sync check failed.");
        }
    }
}

/// <summary>
/// The runtime's "Remember this?" port. The assistant control owns the row while its window is open and sets
/// <see cref="Handler"/>; without one a proposal is dropped for this session (cancelled, never declined) so it can
/// be raised again later.
/// </summary>
public sealed class AgentPrompterRelay : IAgentPrompter
{
    public Func<BrainProposal, CancellationToken, Task<bool>>? Handler { get; set; }

    public Task<bool> ProposeAsync(BrainProposal proposal, CancellationToken ct)
    {
        var handler = Handler;
        if (handler is null) throw new OperationCanceledException("No assistant window is open to show the proposal.");
        return handler(proposal, ct);
    }
}
