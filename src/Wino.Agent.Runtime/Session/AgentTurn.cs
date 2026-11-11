using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Session;

/// <summary>What one request became: a question, a plan ready to confirm, a message in the app's words, or nothing.</summary>
public abstract record AgentTurn
{
    public sealed record Clarify(Clarification Clarification, ResolvedPlanDraft Partial) : AgentTurn;

    /// <summary>Ready to confirm and run (<see cref="Resolved"/>; the nested type name keeps the property from being called Plan).</summary>
    public sealed record Plan(ResolvedPlan Resolved) : AgentTurn;

    public sealed record Message(AgentMessageKind Kind, string? Gloss, IReadOnlyList<AgentFollowUp> FollowUps) : AgentTurn
    {
        public Message(AgentMessageKind kind, string? gloss = null) : this(kind, gloss, []) { }
    }

    public sealed record Nothing : AgentTurn
    {
        public static readonly Nothing Instance = new();
    }
}

public enum AgentMessageKind
{
    Remembered,
    Forgotten,
    ProposalSaved,
    StandingRuleRefused,
    OutOfScope,
    NotUnderstood,
    NeedsPreviousRequest,
    NothingMatched,
    Undone,
    Answer,
    /// <summary>The request named nothing the device has (an account, a calendar) and there was nothing to pick from.</summary>
    Unavailable,
}

/// <summary>A button the app may offer under a message. <see cref="RunOnce"/> re-submits the refused standing rule as a one-time plan (<see cref="Resubmit"/> carries the reading without its condition; pass it as <see cref="AgentRequest.PreRead"/>).</summary>
public sealed record AgentFollowUp(AgentFollowUpKind Kind, string? Gloss, AssistantReading? Resubmit);

public enum AgentFollowUpKind
{
    RunOnce,
}

/// <summary>
/// One request. <see cref="PreRead"/> carries a reading the app already has (the socket's reading frame for a spoken
/// request, or a follow-up's resubmission); otherwise the runtime reads the text through <see cref="Ports.IReadingClient"/>.
/// <see cref="CurrentAccountId"/> is the account the request was typed inside, if any.
/// </summary>
public sealed record AgentRequest(
    string Text,
    AgentInputKind Input = AgentInputKind.Typed,
    AgentSelection? Selection = null,
    Guid? CurrentAccountId = null,
    AssistantReading? PreRead = null);

public enum AgentInputKind
{
    Typed,
    Spoken,
}

/// <summary>
/// The items a request was started from, as ids. <see cref="Senders"/> are the senders of selected mails (or the
/// organizers of selected events), resolved by the app so "reply to the sender" needs no server round trip.
/// </summary>
public sealed record AgentSelection(
    AssistantSelectionKind Kind,
    IReadOnlyList<ItemRef> Items,
    IReadOnlyList<string> Flags,
    IReadOnlyList<Recipient> Senders,
    Guid? AccountId);

public sealed record AgentSessionOptions(string? Name = null);
