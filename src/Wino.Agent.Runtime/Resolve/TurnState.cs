using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Session;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

/// <summary>
/// One turn in progress: the request, the validated reading, the answers given so far (by slot), what was learned
/// and what is proposed. Resolution is re-run from scratch after every answer; the answers make it deterministic.
/// </summary>
public sealed class TurnState
{
    public required AgentRequest Request { get; init; }

    public required AssistantReading Reading { get; init; }

    public required DateTime StartedAt { get; init; }

    /// <summary>Answers by slot key (<c>stepN:field</c>).</summary>
    public Dictionary<string, ClarificationAnswer> Answers { get; } = new(StringComparer.Ordinal);

    /// <summary>The slot key of the clarification currently shown, with the clarification.</summary>
    public (string Field, Clarification Clarification)? Pending { get; set; }

    /// <summary>Brain writes made by this turn's answers and statements (written after each run, replace semantics make re-writes harmless).</summary>
    public List<LearnedEntry> Learned { get; } = [];

    /// <summary>Inferred facts to offer after the plan's outcome.</summary>
    public List<BrainProposal> Proposals { get; } = [];

    /// <summary>Entry ids to forget (confirmed forgets, negations).</summary>
    public List<string> Forget { get; } = [];

    /// <summary>Message kinds the statements produced (Remembered, Forgotten) when there is no plan.</summary>
    public List<AgentMessageKind> MemoryMessages { get; } = [];

    /// <summary>Compose calls to make after resolution, per step.</summary>
    public List<PendingCompose> Composes { get; } = [];

    /// <summary>People named per role, for the conversation context.</summary>
    public Dictionary<string, List<Recipient>> People { get; } = new(StringComparer.Ordinal);

    public int ClarificationsRaised { get; set; }
}

public sealed record LearnedEntry(BrainEntryKind Kind, BrainSubject Subject, string Value, BrainSource Source, string? SaidAs, bool ReplaceOthers = false);

/// <summary>A composed content to write after recipients resolved: the points go to the compose call with the hint built from stated preferences only.</summary>
public sealed record PendingCompose(int StepN, string Slot, List<string> Points, string Kind, string? Language, string? Tone, string? Length, IReadOnlyList<Recipient> Recipients, Guid? AccountId);
