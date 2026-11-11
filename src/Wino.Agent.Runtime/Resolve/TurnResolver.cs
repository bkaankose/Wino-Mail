using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Policy;
using Wino.Agent.Runtime.Ports;
using Wino.Agent.Runtime.Reading;
using Wino.Agent.Runtime.Session;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

/// <summary>
/// Turns a reading into a <see cref="ResolvedPlan"/> over one <see cref="DomainIndex"/> snapshot, raising one typed
/// clarification at a time (<see cref="NeedsClarificationException"/>) and ending a turn early with a message
/// (<see cref="TurnMessageException"/>). Every ref yields candidates, a score and a decision: resolved, ask, or none.
/// A pick beats ranking; a stated fact beats a pick; two entries that disagree ask.
/// </summary>
public sealed partial class TurnResolver(TurnState turn, DomainIndex index, AgentBrain brain, IAgentDomainQueries queries, IAgentClock clock, ConversationContext context)
{
    private readonly TurnState _t = turn;
    private readonly DomainIndex _index = index;
    private readonly AgentBrain _brain = brain;
    private readonly IAgentDomainQueries _q = queries;
    private readonly IAgentClock _clock = clock;
    private readonly ConversationContext _ctx = context;
    private readonly Dictionary<int, AccountScope> _scopes = [];
    private readonly HashSet<string> _asAccount = new(StringComparer.Ordinal);
    private readonly List<DraftStep> _draft = [];

    private AssistantReading R => _t.Reading;
    private DateTime Now => _clock.LocalNow;

    /// <summary>The accounts a step is limited to: null ids = every account.</summary>
    public sealed record AccountScope(IReadOnlyList<Guid>? Ids)
    {
        public static readonly AccountScope All = new((IReadOnlyList<Guid>?)null);
        public bool IsAll => Ids is null;
        public Guid? One => Ids is { Count: 1 } ? Ids[0] : null;
        public static AccountScope Of(Guid id) => new([id]);
    }

    public ResolvedPlanDraft Partial => new(_draft.ToList());

    public async Task<ResolvedPlan> ResolveAsync(CancellationToken ct)
    {
        await ResolveMemoryAsync(ct).ConfigureAwait(false);
        var steps = new List<ResolvedStep>();
        var readingSteps = ApplyDiscourse(R.Steps);
        _lastN = readingSteps.Count == 0 ? 0 : readingSteps.Max(s => s.N);
        foreach (var s in readingSteps) _draft.Add(new DraftStep(s.N, ReadingValidator.ToAction(s.Action), s.Gloss, false));
        foreach (var step in readingSteps)
        {
            ct.ThrowIfCancellationRequested();
            var resolved = await ResolveStepAsync(step, ct).ConfigureAwait(false);
            steps.Add(resolved);
            // A step that resolved to several plans of its own kind (one rule per sender) runs as that many steps, numbered after the reading's.
            if (_extraArgs.TryGetValue(step.N, out var extra))
            {
                foreach (var args in extra)
                {
                    var policy = PlanPolicy.Assess(resolved.Action, resolved.Target, args, resolved.EstimatedCount, _index);
                    steps.Add(resolved with { N = ++_lastN, Args = args, Risk = policy.Risk, NeedsConfirmation = policy.NeedsConfirmation || resolved.NeedsConfirmation, Permanent = policy.Permanent });
                }
            }

            var i = _draft.FindIndex(d => d.N == step.N);
            if (i >= 0) _draft[i] = _draft[i] with { Resolved = true };
        }

        if (R.Discourse?.Undo == true && steps.Count == 0)
        {
            // The items the last plan of this session touched; with none, the app's own undo stack (the same step an explicit Undo reading gets).
            var touched = _ctx.Turns(Now).LastOrDefault(t => t.Outcome is not null)?.Outcome!.Steps.SelectMany(s => s.Touched).ToList() ?? [];
            steps.Add(touched.Count == 0
                ? new ResolvedStep(1, AgentAction.Undo, StepTarget.None.Instance, new StepArgs.SystemArgs(), null, null, RiskLevel.None, false, null, null)
                : new ResolvedStep(1, AgentAction.Undo, new StepTarget.MailItems(touched), StepArgs.NoArgs.Instance, null, null, RiskLevel.None, false, null, touched.Count));
        }

        var gloss = string.Join(" ", steps.Select(s => s.Gloss).Where(g => !string.IsNullOrWhiteSpace(g)));
        return new ResolvedPlan(Guid.NewGuid(), steps, gloss.Length == 0 ? null : gloss, R.Conf, R.Conf < 0.5);
    }

    // ---- discourse -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A correction or an ellipsis of the previous turn: the previous reading's step is merged under the current one
    /// (the current step's slots win), so "no, Friday" and "Jay too" resolve like full requests. A corrected plan
    /// that already ran keeps acting on the items its outcome touched.
    /// </summary>
    private List<AssistantStep> ApplyDiscourse(List<AssistantStep> steps)
    {
        var d = R.Discourse;
        if (d is null || (d.Corrects is null && d.Elides is null)) return steps;
        // A statement that corrects a fact ("no, my father is Mehmet") is not a correction of the previous plan.
        if (steps.Count == 0 && R.Memory is { Count: > 0 }) return steps;
        var previous = _ctx.Last(Now);
        if (previous is null) return steps.Count > 0 ? steps : throw new TurnMessageException(AgentMessageKind.NeedsPreviousRequest);
        var n = d.Corrects?.Step ?? d.Elides!.Step;
        var source = previous.Reading.Steps.FirstOrDefault(s => s.N == n) ?? previous.Reading.Steps.FirstOrDefault();
        // The classifier marks an ellipsis freely; without a previous step there is nothing to copy and a complete step stands on its own.
        if (source is null) return steps.Count > 0 ? steps : throw new TurnMessageException(AgentMessageKind.NeedsPreviousRequest);
        // "archive them" after a search that ran: the ellipsis names the items the previous turn found, not its query.
        // A correction of a plan that ran acts on the items it touched (plan 4.7), the same way.
        if (steps.Count > 0 && previous.Outcome is not null && !HasOwnFilters(steps[0], source) && ItemPointsBack(steps[0]) && ActionCatalog.ItemKindOf(ReadingValidator.ToAction(steps[0].Action)) is { } kind
            && !ActionCatalog.Creates(ReadingValidator.ToAction(steps[0].Action)) && _ctx.LastTouched(Now, kind).Count > 0)
        {
            _previousItemsSteps.Add(steps[0].N);
            return steps;
        }
        // The previous refs come along under prefixed ids so they cannot collide.
        var prefix = "p" + n + "_";
        var refs = R.Refs.ToList();
        foreach (var r in previous.Reading.Refs)
            refs.Add(r with { Id = prefix + r.Id, Members = r.Members?.Select(m => prefix + m).ToList(), Time = r.Time is { RelTo: { } rel } t ? t with { RelTo = prefix + rel } : r.Time });
        _merged = R with { Refs = refs };
        var current = steps.Count > 0 ? steps[0] : new AssistantStep(1, source.Action, null, null, null, false, null, [], R.Conf, null, null);
        var args = current.Args.ToList();
        foreach (var a in source.Args)
        {
            if (args.Any(x => x.Name == a.Name)) continue;
            args.Add(new AssistantArg(a.Name, a.Ref is null ? null : prefix + a.Ref, a.Refs?.Select(x => prefix + x).ToList(), a.Value));
        }

        var on = current.On ?? (source.On is { } so && so != AssistantStep.OnSelection ? prefix + so : source.On);
        var mergedStep = current with { Action = AssistantActionCatalog.IsKnown(current.Action) && steps.Count > 0 ? current.Action : source.Action, On = on, Scope = current.Scope ?? source.Scope, Args = args, Gloss = current.Gloss ?? source.Gloss };
        var rest = steps.Skip(1).ToList();
        return [mergedStep, .. rest];
    }

    private AssistantReading? _merged;

    /// <summary>Steps whose ellipsis means the items the previous turn's outcome touched.</summary>
    private readonly HashSet<int> _previousItemsSteps = [];

    /// <summary>Argument names that select which existing items a step is about (as opposed to what to change).</summary>
    private static readonly HashSet<string> FilterArgs = new(StringComparer.Ordinal) { "sender", "recipient", "folder", "account", "period", "when", "attendee", "due", "calendar", "list", "person" };

    /// <summary>
    /// Whether a step names its own items (a sender, a folder, a period...) rather than pointing back at the previous
    /// turn. A filter the classifier merely repeated from the previous step (the same argument with the same words) is
    /// not the step's own.
    /// </summary>
    private bool HasOwnFilters(AssistantStep step, AssistantStep previous)
    {
        foreach (var a in step.Args.Where(a => FilterArgs.Contains(a.Name) && (a.Ref is not null || a.Refs is { Count: > 0 })))
        {
            var mine = (a.Refs ?? (a.Ref is null ? [] : [a.Ref])).Select(R.Ref).OfType<AssistantRef>().Select(r => TextMatch.Key(r.Norm ?? r.Text)).Where(k => k.Length > 0).ToList();
            var theirs = previous.Args.Where(p => p.Name == a.Name).SelectMany(p => p.Refs ?? (p.Ref is null ? [] : [p.Ref]))
                .Select(id => _ctx.Last(Now)?.Reading.Ref(id)).OfType<AssistantRef>().SelectMany(r => new[] { TextMatch.Key(r.Norm), TextMatch.Key(r.Text) }).ToHashSet(StringComparer.Ordinal);
            if (mine.Count == 0 || mine.Any(k => !theirs.Contains(k))) return true;
        }

        var item = step.On is { } on && on != AssistantStep.OnSelection ? R.Ref(on) : null;
        return item is not null && item.By != AssistantRefBy.Pronoun && (item.Time is not null || !string.IsNullOrEmpty(item.Props?.Thread));
    }

    /// <summary>
    /// Whether the item ref of an elliptical step points back at the previous items rather than naming a set anew: no
    /// item, a pronoun, words the request does not contain (the classifier copied them from the previous turn), or a
    /// short bare reference ("all", "the first one") with no kind of mail of its own. A noun the user said again
    /// ("delete the promos" after "archive the promos") is a new query.
    /// </summary>
    private bool ItemPointsBack(AssistantStep step)
    {
        if (step.On == AssistantStep.OnSelection) return true;
        var item = step.On is { } on ? R.Ref(on) : null;
        if (item is null || item.By == AssistantRefBy.Pronoun) return true;
        if (!TextMatch.Quoted(_t.Request.Text, item.Text)) return true;
        return TextMatch.Words(item.Text).Length <= 3 && string.IsNullOrEmpty(item.Props?.ItemType)
            && (string.IsNullOrEmpty(item.Props?.Topic) || !TextMatch.Quoted(_t.Request.Text, item.Props.Topic));
    }

    /// <summary>Refs the resolver made itself (a set built from several statements).</summary>
    private readonly Dictionary<string, AssistantRef> _synthetic = new(StringComparer.Ordinal);

    private AssistantRef? Ref(string? id) => id is not null && _synthetic.TryGetValue(id, out var s) ? s : (_merged ?? R).Ref(id);

    private IEnumerable<AssistantRef> Refs(AssistantStep step, string name)
    {
        foreach (var id in step.RefIds(name))
            if (Ref(id) is { } r) yield return r;
    }

    private IEnumerable<AssistantRef> Expand(AssistantRef r)
    {
        if (r.Members is { Count: > 0 })
        {
            foreach (var m in r.Members) if (Ref(m) is { } mr) yield return mr;
        }
        else yield return r;
    }

    // ---- steps ---------------------------------------------------------------------------------------------------

    private async Task<ResolvedStep> ResolveStepAsync(AssistantStep step, CancellationToken ct)
    {
        step = LinkPronounToEarlierFind(step);
        (var action, step) = await ChooseActionAsync(step, ct).ConfigureAwait(false);
        if (action is AgentAction.AddContactToList or AgentAction.RemoveContactFromList) await TryGroupEditAsync(step, action == AgentAction.AddContactToList, ct).ConfigureAwait(false);
        var scope = await ResolveAccountScopeAsync(step, action, ct).ConfigureAwait(false);
        _scopes[step.N] = scope;
        StepTarget target;
        int? count = null;
        int? dependsOn = step.From?.Step;
        var linkPart = step.From?.Part;
        if (step.From is { } from)
        {
            if (_scopes.TryGetValue(from.Step, out var inherited) && scope.IsAll) scope = inherited;
            // Items an earlier step already holds (the selection, the previous turn's results): every step acts on those
            // very items, in the user's order and independently, so a failed first step does not stop the second.
            if (from.Part == AssistantStepLinkParts.Items && !ActionCatalog.Creates(action) && _resolved.TryGetValue(from.Step, out var source) && ItemsOf(source.Target) is { Count: > 0 } items
                && ActionCatalog.ItemKindOf(action) is { } wanted && items.All(i => i.Kind == wanted || (wanted == ItemKind.Draft && i.Kind == ItemKind.Mail)))
            {
                target = ItemsTarget(wanted, items);
                count = items.Count;
                dependsOn = null;
                linkPart = null;
            }
            else target = new StepTarget.FromStep(from.Step, from.Part);
        }
        else
        {
            (target, count) = await ResolveTargetAsync(step, action, scope, ct).ConfigureAwait(false);
        }

        if (target is StepTarget.Mails tm && scope.IsAll && tm.AccountId is { } ta) scope = AccountScope.Of(ta);
        var args = await ResolveArgsAsync(step, action, scope, target, ct).ConfigureAwait(false);
        var policy = PlanPolicy.Assess(action, target, args, count, _index);
        // A destructive or outgoing action the reader did not put first is the device's word, not the user's: it is confirmed whatever its risk.
        var resolved = new ResolvedStep(step.N, action, target, args, dependsOn, linkPart, policy.Risk, policy.NeedsConfirmation || _switchedRisky.Contains(step.N), step.Gloss, count, policy.Permanent);
        _resolved[step.N] = resolved;
        return resolved;
    }

    private readonly Dictionary<int, ResolvedStep> _resolved = [];

    /// <summary>Steps whose action was switched, without a question, to a destructive or outgoing candidate the reader did not put first.</summary>
    private readonly HashSet<int> _switchedRisky = [];

    /// <summary>Further argument sets a step resolved to beside its own (one mail rule per sender); each becomes a step of its own.</summary>
    private readonly Dictionary<int, List<StepArgs>> _extraArgs = [];

    /// <summary>The highest step number handed out so far (the reading's, then the split steps').</summary>
    private int _lastN;

    /// <summary>
    /// "find the contacts at X and email them": a pronoun recipient of a later step with no previous turn to bind it is
    /// the people an earlier step of this turn finds; the step becomes a link from that step's people (or its items'
    /// sender) and the pronoun leaves its argument.
    /// </summary>
    private AssistantStep LinkPronounToEarlierFind(AssistantStep step)
    {
        if (step.N == 1 || step.From is not null || _ctx.LastPeople(Now, null).Count > 0) return step;
        var pronounArgs = step.Args.Where(a => a.Name is "to" or "attendees" && (a.Refs ?? (a.Ref is null ? [] : [a.Ref])).Select(Ref).All(r => r is { By: AssistantRefBy.Pronoun })).ToList();
        if (pronounArgs.Count == 0) return step;
        var finder = R.Steps.LastOrDefault(s => s.N < step.N && AssistantActionCatalog.IsFind(s.Action));
        if (finder is null) return step;
        var part = ReadingValidator.ToAction(finder.Action) == AgentAction.FindContacts ? AssistantStepLinkParts.People : AssistantStepLinkParts.Sender;
        return step with { From = new AssistantStepLink(finder.N, part), On = null, Args = step.Args.Where(a => !pronounArgs.Contains(a)).ToList() };
    }

    private static IReadOnlyList<ItemRef>? ItemsOf(StepTarget target) => target switch
    {
        StepTarget.MailItems m => m.Items,
        StepTarget.EventItems e => e.Items,
        StepTarget.TaskItems t => t.Items,
        StepTarget.ContactItems c => c.Items,
        _ => null,
    };

    private static StepTarget ItemsTarget(ItemKind kind, IReadOnlyList<ItemRef> items) => kind switch
    {
        ItemKind.Event => new StepTarget.EventItems(items),
        ItemKind.Task => new StepTarget.TaskItems(items),
        ItemKind.Contact => new StepTarget.ContactItems(items),
        _ => new StepTarget.MailItems(items),
    };

    /// <summary>Refs of a kind the classifier attached to no step (neither in args nor in <c>on</c>, nor as a set member).</summary>
    private List<AssistantRef> OrphanRefs(string kind) => R.Refs.Where(r => r.Kind == kind
        && !R.Steps.Any(s => s.On == r.Id || s.Args.Any(a => a.Ref == r.Id || a.Refs?.Contains(r.Id) == true))
        && !R.Refs.Any(x => x.Members?.Contains(r.Id) == true)).ToList();

    // ---- clarifications ------------------------------------------------------------------------------------------

    private sealed record Consumed(IReadOnlyList<ItemRef> Refs, IReadOnlyList<Candidate> Chosen, string? NewAddress);

    /// <summary>Uses the answer to this slot when there is one, otherwise raises the question (the session returns it and re-runs on the answer).</summary>
    private Consumed Consume(int stepN, string field, ClarificationKind kind, string saidAs, IReadOnlyList<Candidate> options,
        bool many = false, bool searchable = true, bool newAddress = false, Candidate? suggested = null, string? filter = null)
    {
        var key = $"{stepN}:{field}";
        if (_t.Answers.TryGetValue(key, out var answer))
        {
            if (answer.Cancelled) throw new TurnMessageException(AgentMessageKind.NothingMatched);
            var refs = answer.Keys.Select(ItemRef.Parse).OfType<ItemRef>().ToList();
            var chosen = refs.Select(r => options.FirstOrDefault(o => o.Ref.Key == r.Key) ?? new Candidate(r, r.Id, null, null, 1, true)).ToList();
            return new Consumed(refs, chosen, answer.NewAddress);
        }

        var shown = options.Take(Ranking.MaxOptions).Select(o => o.ToOption()).ToList();
        var clarification = new Clarification(Guid.NewGuid(), stepN, field, kind, saidAs, shown, many, searchable, newAddress, suggested?.ToOption()) { Filter = filter };
        _t.Pending = (key, clarification);
        _t.ClarificationsRaised++;
        throw new NeedsClarificationException(clarification);
    }

    private static string SaidAs(AssistantRef r) => r.Text ?? r.Norm ?? r.Kind;

    private void Learn(BrainEntryKind kind, BrainSubject subject, string value, BrainSource source, string? saidAs, bool replace = false)
        => _t.Learned.Add(new LearnedEntry(kind, subject, value, source, saidAs, replace));

    private void AddPeople(string role, IEnumerable<Recipient> people)
    {
        if (!_t.People.TryGetValue(role, out var list)) _t.People[role] = list = [];
        foreach (var p in people) if (!list.Any(x => string.Equals(x.Address, p.Address, StringComparison.OrdinalIgnoreCase))) list.Add(p);
    }

    // ---- account scope -------------------------------------------------------------------------------------------

    private async Task<AccountScope> ResolveAccountScopeAsync(AssistantStep step, AgentAction action, CancellationToken ct)
    {
        var refs = Refs(step, "account").ToList();
        // An account ref the classifier left unattached ("the test folder in my personal account" with the account beside the step).
        if (refs.Count == 0 && step.N == 1) refs = OrphanRefs(AssistantRefKinds.Account);
        // An unknown ref used as the thing to show ("show Personal") may be an account.
        if (refs.Count == 0 && step.On is { } on && on != AssistantStep.OnSelection && Ref(on) is { Kind: AssistantRefKinds.Unknown } u && (u.Kinds ?? []).Contains(AssistantRefKinds.Account))
        {
            var pick = await ResolveUnknownAsync(step, u, "account", ct).ConfigureAwait(false);
            if (pick is { Kind: ItemKind.Account } acc) return AccountScope.Of(acc.AccountId!.Value);
        }

        if (refs.Count == 0)
        {
            // "show Personal": a container named like a stated account nickname could be either; ask which kind, remember the pick.
            var named = Refs(step, "folder").Concat(step.On is { } on2 && on2 != AssistantStep.OnSelection && Ref(on2) is { } onRef ? [onRef] : []).Concat(step.N == 1 ? OrphanRefs(AssistantRefKinds.Container) : [])
                .FirstOrDefault(r => r.Kind == AssistantRefKinds.Container && r.By == AssistantRefBy.Name && AccountCandidates(r).Any(c => c.Exact && c.Score >= 1));
            if (named is not null)
            {
                var pick = await ResolveUnknownAsync(step, named with { Kind = AssistantRefKinds.Unknown, Kinds = [AssistantRefKinds.Account, AssistantRefKinds.Container] }, "kind", ct).ConfigureAwait(false);
                if (pick is { Kind: ItemKind.Account } pa)
                {
                    _asAccount.Add(named.Id);
                    return AccountScope.Of(pa.AccountId!.Value);
                }
            }

            // "from Gmail" read as a folder: a container ref that names no container but exactly one account is that account.
            foreach (var r in Refs(step, "folder").Concat(step.N == 1 ? OrphanRefs(AssistantRefKinds.Container) : []).Where(r => r.By == AssistantRefBy.Name && r.Props?.WellKnown is null))
            {
                if (ContainerCandidates(r, null, null).Any(c => c.Score >= 0.7)) continue;
                var accounts = AccountCandidates(r).Where(c => c.Score >= 0.8).DistinctBy(c => c.Ref.Key).ToList();
                if (accounts.Count != 1) continue;
                _asAccount.Add(r.Id);
                return AccountScope.Of(accounts[0].Ref.AccountId!.Value);
            }

            if (step.On == AssistantStep.OnSelection && _t.Request.Selection?.AccountId is { } sel) return AccountScope.Of(sel);
            return AccountScope.All;
        }

        var ids = new List<Guid>();
        foreach (var r in refs)
        {
            if (r.By == AssistantRefBy.All) return AccountScope.All;
            var account = await ResolveAccountAsync(step, r, "account", ct).ConfigureAwait(false);
            if (account is not null) ids.Add(account.Id);
        }

        return ids.Count == 0 ? AccountScope.All : new AccountScope(ids.Distinct().ToList());
    }

    /// <summary>Resolves one account ref (plan 4.4 account rows). Null only for <c>all</c>.</summary>
    private async Task<DomainIndex.AccountEntry?> ResolveAccountAsync(AssistantStep step, AssistantRef r, string field, CancellationToken ct)
    {
        var accounts = _index.Accounts;
        if (accounts.Count == 0) throw new TurnMessageException(AgentMessageKind.Unavailable, SaidAs(r));
        var norm = TextMatch.Fold(r.Norm);
        var text = TextMatch.Fold(r.Text);
        var key = TextMatch.Key(r.Norm ?? r.Text);
        var candidates = new List<Candidate>();
        Candidate C(DomainIndex.AccountEntry a, double score, bool exact) => new(ItemRef.Account(a.Id), a.Name, a.Address, null, score, exact);

        // A stated nickname for these words beats every ranking ("my Outlook" when one of three Outlook accounts is nicknamed so).
        if (r.By is not (AssistantRefBy.All or AssistantRefBy.Current or AssistantRefBy.Address or AssistantRefBy.Domain) && key.Length > 0)
        {
            var nicknamed = _brain.Find(BrainEntryKind.AccountNickname)
                .Where(e => NicknameMatches(e, r))
                .Select(e => _index.Account(e.Subject.AccountId)).OfType<DomainIndex.AccountEntry>().DistinctBy(a => a.Id).ToList();
            if (nicknamed.Count == 1) return nicknamed[0];
        }

        switch (r.By)
        {
            case AssistantRefBy.All:
                return null;
            case AssistantRefBy.Current:
                if (_t.Request.CurrentAccountId is { } current && _index.Account(current) is { } cur) return cur;
                break;
            case AssistantRefBy.Ordinal:
                var n = r.Props?.Ordinal ?? (int.TryParse(norm, out var parsed) ? parsed : 0);
                var ordered = accounts.OrderBy(a => a.Account.Order).ToList();
                if (n >= 1 && n <= ordered.Count) return ordered[n - 1];
                break;
            case AssistantRefBy.Address or AssistantRefBy.Domain:
                foreach (var a in accounts)
                {
                    if (a.AllAddresses.Any(x => TextMatch.Same(x, r.Norm) || TextMatch.Same(x, r.Text))) candidates.Add(C(a, 1, true));
                    else if (a.AllAddresses.Any(x => TextMatch.Domain(x) == norm || TextMatch.Domain(x) == text)) candidates.Add(C(a, 0.9, false));
                    else if (norm.Length >= 4 && a.AllAddresses.Any(x => TextMatch.Fold(x).Contains(norm))) candidates.Add(C(a, 0.85, false));
                }

                break;
            case AssistantRefBy.Provider:
                foreach (var a in accounts)
                {
                    var providerName = TextMatch.Fold(a.Account.ProviderType.ToString());
                    var domainWords = TextMatch.Words(TextMatch.Domain(a.Address));
                    var words = TextMatch.Words(r.Norm ?? r.Text);
                    var hit = words.Any(w => w == providerName || domainWords.Contains(w) || (w.Length >= 4 && providerName.StartsWith(w)));
                    if (hit) candidates.Add(C(a, 1, true));
                }

                if (candidates.Count > 1)
                {
                    // Several accounts share the provider: ask; the pick is only remembered through a proposal (it is an inference).
                    var byPick = _brain.FindPick(key, ItemKind.Account);
                    if (byPick is not null && ItemRef.Parse(byPick.Value) is { } picked && _index.Account(picked.AccountId) is { } pa) return pa;
                    var chosen = Consume(step.N, field, ClarificationKind.PickAccount, SaidAs(r), candidates.Select(c => c with { Score = 0.5 }).ToList(), searchable: false);
                    var chosenAccount = chosen.Refs.Select(x => _index.Account(x.AccountId)).FirstOrDefault(x => x is not null);
                    if (chosenAccount is null) break;
                    _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.Pick, BrainSubject.Words(key, ItemKind.Account), ItemRef.Account(chosenAccount.Id).Key, SaidAs(r)));
                    return chosenAccount;
                }

                break;
            default:
                // nickname, purpose, name: stated nickname, then pick, then the account's own name.
                foreach (var e in _brain.Find(BrainEntryKind.AccountNickname))
                {
                    if (NicknameMatches(e, r) && _index.Account(e.Subject.AccountId) is { } a)
                        candidates.Add(C(a, 1, true));
                }

                if (_brain.FindPick(key, ItemKind.Account) is { } pick && ItemRef.Parse(pick.Value) is { } pr && _index.Account(pr.AccountId) is { } pa2 && !candidates.Any(c => c.Exact))
                    candidates.Add(C(pa2, 0.95, true));
                foreach (var a in accounts)
                {
                    var name = TextMatch.Fold(a.Name);
                    if (name.Length == 0) continue;
                    if (name == norm || name == text) candidates.Add(C(a, 0.9, false));
                    else if (TextMatch.NameScore(TextMatch.Words(r.Norm ?? r.Text), TextMatch.Words(a.Name)) > 0) candidates.Add(C(a, 0.7, false));
                    else if (a.AllAddresses.Any(x => TextMatch.Same(x, r.Norm) || TextMatch.Same(x, r.Text) || (norm.Length >= 4 && TextMatch.Fold(x).Contains(norm)))) candidates.Add(C(a, 0.85, false));
                    else if (TextMatch.Words(r.Norm ?? r.Text).Any(w => w == TextMatch.Fold(a.Account.ProviderType.ToString()) || TextMatch.Words(TextMatch.Domain(a.Address)).Contains(w))) candidates.Add(C(a, 0.6, false));
                }

                break;
        }

        var (decision, top, ranked) = Ranking.Decide(candidates, 0.9, 0.3);
        if (decision == Decision.Resolved) return _index.Account(top!.Ref.AccountId);
        var options = ranked.Count > 0 ? ranked : accounts.Select(a => C(a, 0, false)).ToList();
        var answer = Consume(step.N, field, ClarificationKind.PickAccount, SaidAs(r), options, searchable: false, suggested: ranked.FirstOrDefault());
        var result = answer.Refs.Select(x => _index.Account(x.AccountId)).FirstOrDefault(x => x is not null);
        if (result is null) throw new TurnMessageException(AgentMessageKind.Unavailable, SaidAs(r));
        if (r.By is AssistantRefBy.Nickname or AssistantRefBy.Purpose && TextMatch.IsConcept(r.Norm))
            Learn(BrainEntryKind.AccountNickname, BrainSubject.Account(result.Id, result.Name), TextMatch.Fold(r.Norm), BrainSource.Pick, r.Text);
        else if (key.Length > 0)
            Learn(BrainEntryKind.Pick, BrainSubject.Words(key, ItemKind.Account), ItemRef.Account(result.Id).Key, BrainSource.Pick, r.Text);
        return result;
    }

    /// <summary>A stated nickname answers the user's words for it: the same words, or the concept inside them ("sandbox account" taught, "sandbox" asked).</summary>
    private static bool NicknameMatches(BrainEntry e, AssistantRef r)
    {
        if (TextMatch.Same(e.Value, r.Norm) || TextMatch.Same(e.Value, r.Text) || TextMatch.Key(e.Value) == TextMatch.Key(r.Norm ?? r.Text)) return true;
        // The concept inside the words, on the norm only ("sandbox" in "sandbox account"; never "work" in "the test folder in Work").
        var value = TextMatch.Words(e.Value);
        var norm = TextMatch.Words(r.Norm);
        if (value.Length == 0 || norm.Length == 0 || value.Length > 3 || norm.Length > 3) return false;
        var (shorter, longer) = value.Length <= norm.Length ? (value, norm) : (norm, value);
        return shorter.All(longer.Contains);
    }

    /// <summary>
    /// The account a new draft, task or event goes to when none is named: the stated default, else the selected items'
    /// account (something made from a selection goes with it), else the current account, else the only one that can hold
    /// it, else a question (and a proposal to make the pick the default).
    /// </summary>
    private DomainIndex.AccountEntry? DefaultAccount(AssistantStep step, string use, string saidAs)
    {
        if (_brain.Find(BrainEntryKind.Default).FirstOrDefault(e => e.Value.StartsWith(use + "=", StringComparison.Ordinal)) is { } d
            && ItemRef.Parse(d.Value[(use.Length + 1)..]) is { } dr && _index.Account(dr.AccountId) is { } da)
            return da;
        var eligible = _index.Accounts.Where(a => use switch
        {
            "task" => a.TaskLists.Count > 0,
            "calendar" => a.Calendars.Any(c => !c.IsReadOnly),
            _ => true,
        }).ToList();
        if (_t.Request.Selection?.AccountId is { } selected && eligible.FirstOrDefault(a => a.Id == selected) is { } sa) return sa;
        if (_t.Request.CurrentAccountId is { } cur && eligible.FirstOrDefault(a => a.Id == cur) is { } ca) return ca;
        if (eligible.Count == 1) return eligible[0];
        if (eligible.Count == 0) return null;
        var options = eligible.Select(a => new Candidate(ItemRef.Account(a.Id), a.Name, a.Address, null, 0, false)).ToList();
        var answer = Consume(step.N, "account", ClarificationKind.PickAccount, saidAs, options, searchable: false);
        var chosen = answer.Refs.Select(x => _index.Account(x.AccountId)).FirstOrDefault(x => x is not null);
        if (chosen is null) return null;
        _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.Default, BrainSubject.Self(), $"{use}={ItemRef.Account(chosen.Id).Key}", saidAs));
        return chosen;
    }
}
