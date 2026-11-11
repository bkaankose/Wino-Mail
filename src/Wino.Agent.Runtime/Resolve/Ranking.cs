using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;

namespace Wino.Agent.Runtime.Resolve;

/// <summary>A thing a reference could mean, with its score in [0, 1]. <see cref="Exact"/>: no ranking guess was involved (an exact name, a pick, a stated fact).</summary>
public sealed record Candidate(ItemRef Ref, string Title, string? Detail, string? Group, double Score, bool Exact)
{
    public ClarificationOption ToOption() => new(Ref.Key, Title, Detail, Group, Ref, Score);
}

public enum Decision
{
    Resolved,
    Ask,
    None,
}

/// <summary>Ask thresholds (plan 4.4): resolve when the top score reaches the minimum and the runner-up is far enough behind.</summary>
public static class Ranking
{
    public const int MaxOptions = 50;

    public static (Decision Decision, Candidate? Top, List<Candidate> Ranked) Decide(IEnumerable<Candidate> candidates, double resolveMin, double margin)
    {
        var ranked = candidates
            .GroupBy(c => c.Ref.Key)
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ranked.Count == 0) return (Decision.None, null, ranked);
        var top = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Score : 0;
        if (top.Score >= resolveMin && top.Score - second >= margin) return (Decision.Resolved, top, ranked);
        if (top.Score >= 0.999 && ranked.Count(c => c.Score >= 0.999) == 1) return (Decision.Resolved, top, ranked);
        return (Decision.Ask, top, ranked);
    }
}

/// <summary>Thrown inside resolution when a question must be asked; the session turns it into a <c>Clarify</c> turn.</summary>
public sealed class NeedsClarificationException(Clarification clarification) : Exception("clarification needed: " + clarification.Field)
{
    public Clarification Clarification { get; } = clarification;
}

/// <summary>Thrown inside resolution when the turn ends in a message rather than a plan.</summary>
public sealed class TurnMessageException(Session.AgentMessageKind kind, string? gloss = null) : Exception(kind.ToString())
{
    public Session.AgentMessageKind Kind { get; } = kind;
    public string? Gloss { get; } = gloss;
}
