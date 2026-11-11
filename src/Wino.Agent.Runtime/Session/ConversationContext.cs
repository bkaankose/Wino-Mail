using Wino.Agent.Runtime.Plan;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Session;

/// <summary>
/// The last turns (5, for 10 minutes): request text, reading, resolved plan, outcome and the people each step
/// named. Memory only, never persisted; cleared by <c>Reset()</c> and on dispose. Only words and action names ever
/// go back to the server (<see cref="Previous"/>); ids and addresses stay here.
/// </summary>
public sealed class ConversationContext
{
    public const int MaxTurns = 5;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public sealed class Turn
    {
        public required DateTime At { get; init; }
        public required string Text { get; init; }
        public required AssistantReading Reading { get; init; }
        public ResolvedPlan? Plan { get; set; }
        public PlanOutcome? Outcome { get; set; }
        /// <summary>People per role (<c>to</c>, <c>cc</c>, <c>attendee</c>, <c>sender</c>...) the resolved steps named.</summary>
        public Dictionary<string, List<Recipient>> People { get; } = new(StringComparer.Ordinal);
        public List<string> Actions => Plan?.Steps.Select(s => s.Action.ToString()).ToList() ?? Reading.Steps.Select(s => s.Action).ToList();
    }

    private readonly List<Turn> _turns = [];

    public IReadOnlyList<Turn> Turns(DateTime now)
    {
        lock (_turns)
        {
            _turns.RemoveAll(t => now - t.At > MaxAge);
            return _turns.ToList();
        }
    }

    public Turn? Last(DateTime now) => Turns(now).LastOrDefault();

    public Turn? LastWithPlan(DateTime now) => Turns(now).LastOrDefault(t => t.Plan is not null);

    public void Add(Turn turn)
    {
        lock (_turns)
        {
            _turns.Add(turn);
            while (_turns.Count > MaxTurns) _turns.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_turns) _turns.Clear();
    }

    /// <summary>The previous turns for the read request: at most two within ten minutes, words and action names only.</summary>
    public List<AssistantPreviousTurn>? Previous(DateTime now)
    {
        var recent = Turns(now).TakeLast(AssistantReadRequest.MaxPreviousTurns)
            .Select(t => new AssistantPreviousTurn(t.Text, t.Actions, (int)Math.Max(0, (now - t.At).TotalSeconds)))
            .Where(p => p.AgoSeconds <= AssistantReadRequest.PreviousMaxAgeSeconds)
            .ToList();
        return recent.Count == 0 ? null : recent;
    }

    /// <summary>The items the last outcome touched, of one kind.</summary>
    public IReadOnlyList<ItemRef> LastTouched(DateTime now, ItemKind kind)
        => Turns(now).LastOrDefault(t => t.Outcome is not null)?.Outcome!.Steps.SelectMany(s => s.Touched).Where(i => i.Kind == kind).ToList() ?? [];

    public IReadOnlyList<Recipient> LastPeople(DateTime now, string? role)
    {
        foreach (var t in Turns(now).Reverse())
        {
            if (role is not null && t.People.TryGetValue(role, out var same) && same.Count > 0) return same;
            var any = t.People.Values.SelectMany(x => x).DistinctBy(r => r.Address.ToLowerInvariant()).ToList();
            if (any.Count > 0) return any;
        }

        return [];
    }
}
