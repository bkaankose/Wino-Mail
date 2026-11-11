using Wino.Agent.Runtime.Plan;

namespace Wino.Agent.Runtime.Clarify;

/// <summary>
/// A typed question the app renders with its own words and pickers. <see cref="Field"/> names the slot
/// (<c>account</c>, <c>to[0]</c>, <c>folder</c>, <c>calendar</c>, <c>event</c>, <c>start</c>, <c>action</c>,
/// <c>memory[0]</c>) so the app can highlight it; <see cref="SaidAs"/> is the user's words for it. One clarification
/// at a time, in step order; answering returns the next one or the plan. Cancelling cancels the turn.
/// </summary>
public sealed record Clarification(
    Guid Id,
    int StepN,
    string Field,
    ClarificationKind Kind,
    string SaidAs,
    IReadOnlyList<ClarificationOption> Options,
    bool AllowsMany,
    bool Searchable,
    bool AllowsNewAddress,
    ClarificationOption? Suggested)
{
    /// <summary>The picker's filter text for a searchable question (the user's words, base form first).</summary>
    public string? Filter { get; init; }
}

public enum ClarificationKind
{
    PickPerson,
    PickPeople,
    PickSender,
    PickAccount,
    PickContainer,
    ChooseKind,
    ChooseItem,
    ChooseAction,
    ConfirmTime,
    ConfirmForget,
}

/// <summary><see cref="Key"/> is <see cref="ItemRef.Key"/>; <see cref="Group"/> is the account name for containers.</summary>
public sealed record ClarificationOption(string Key, string Title, string? Detail, string? Group, ItemRef Ref, double Score);

/// <summary>
/// The user's answer: the chosen option keys, or a new address typed into a person picker, or a cancel.
/// For <see cref="ClarificationKind.ConfirmTime"/> a key of <see cref="ItemKind.Time"/> with an ISO time.
/// </summary>
public sealed record ClarificationAnswer(Guid Id, IReadOnlyList<string> Keys, string? NewAddress = null, bool Cancelled = false)
{
    public static ClarificationAnswer Pick(Guid id, params string[] keys) => new(id, keys);
    public static ClarificationAnswer Cancel(Guid id) => new(id, [], null, true);
}
