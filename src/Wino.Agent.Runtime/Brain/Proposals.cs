using Wino.Agent.Runtime.Ports;

namespace Wino.Agent.Runtime.Brain;

/// <summary>
/// The "Remember this?" lifecycle (plan 4.8): inferred facts wait here (at most 3, oldest dropped) until the plan's
/// outcome arrives, then the prompter shows each as a yes/no. Yes writes the entry with <see cref="BrainSource.Proposal"/>;
/// no writes a <see cref="BrainEntryKind.Declined"/> entry so the same proposal is not raised for 30 days.
/// </summary>
public sealed class Proposals(AgentBrain brain)
{
    public const int MaxPending = 3;

    private readonly List<BrainProposal> _pending = [];

    public IReadOnlyList<BrainProposal> Pending
    {
        get { lock (_pending) return _pending.ToList(); }
    }

    public void Queue(BrainProposal proposal)
    {
        if (brain.IsDeclined(proposal.Signature)) return;
        var subjectKey = proposal.Subject.Key;
        if (brain.Find(proposal.Kind, proposal.Value).Any(e => e.Subject.Key == subjectKey)) return;
        lock (_pending)
        {
            if (_pending.Any(p => p.Signature == proposal.Signature)) return;
            _pending.Add(proposal);
            while (_pending.Count > MaxPending) _pending.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_pending) _pending.Clear();
    }

    /// <summary>Shows every pending proposal through the prompter and writes the answers.</summary>
    public async Task ShowAsync(IAgentPrompter? prompter, CancellationToken ct)
    {
        List<BrainProposal> batch;
        lock (_pending) { batch = _pending.ToList(); _pending.Clear(); }
        if (prompter is null) return;
        foreach (var p in batch)
        {
            bool yes;
            try { yes = await prompter.ProposeAsync(p, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await AnswerAsync(p, yes, ct).ConfigureAwait(false);
        }
    }

    public async Task AnswerAsync(BrainProposal proposal, bool yes, CancellationToken ct)
    {
        if (yes) await brain.RememberAsync(proposal.Kind, proposal.Subject, proposal.Value, BrainSource.Proposal, proposal.SaidAs, ct).ConfigureAwait(false);
        else await brain.RememberAsync(BrainEntryKind.Declined, BrainSubject.Words(proposal.Signature, null), proposal.Signature, BrainSource.Pick, null, ct).ConfigureAwait(false);
    }
}
