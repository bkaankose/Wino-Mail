using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Reading;
using Wino.Agent.Runtime.Session;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

public sealed partial class TurnResolver
{
    private static ItemKind? KindOfType(string? type) => type switch
    {
        AssistantContainerTypes.Calendar => ItemKind.Calendar,
        AssistantContainerTypes.TaskList => ItemKind.TaskList,
        AssistantContainerTypes.Category => ItemKind.Category,
        AssistantContainerTypes.ContactList => ItemKind.ContactList,
        AssistantContainerTypes.Folder or AssistantContainerTypes.Label => ItemKind.Folder,
        _ => null,
    };

    private string? GroupOf(DomainIndex.Container c) => _index.AccountName(c.AccountId);

    private Candidate ContainerToCandidate(DomainIndex.Container c, double score, bool exact)
        => new(c.Ref, c.Name, c.Kind == ItemKind.Folder ? c.Path : c.Kind.ToString(), GroupOf(c), score, exact);

    /// <summary>Container candidates by name (plan 4.4 container row): synonym 1.0, pick 0.95, exact name 0.95, contains 0.7; well-known containers 1.0 each.</summary>
    private IEnumerable<Candidate> ContainerCandidates(AssistantRef r, ItemKind? kind, IReadOnlyList<Guid>? scope)
    {
        var kinds = kind is { } k ? [k] : KindOfType(r.Props?.Type) is { } typed ? [typed] : new[] { ItemKind.Folder, ItemKind.Calendar, ItemKind.TaskList, ItemKind.Category, ItemKind.ContactList };
        var norm = TextMatch.Fold(r.Norm);
        var text = TextMatch.Fold(r.Text);
        var key = TextMatch.Key(r.Norm ?? r.Text);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Candidate? Emit(DomainIndex.Container c, double score, bool exact) => seen.Add(c.Ref.Key + score) ? ContainerToCandidate(c, score, exact) : null;

        if (r.Props?.WellKnown is { } wk)
        {
            // "the Outbox" read as a well-known folder: a folder named exactly so wins over the guess.
            var exactNamed = kinds.SelectMany(k2 => _index.ContainersOf(k2, scope)).Where(c => TextMatch.Same(c.Name, r.Norm) || TextMatch.Same(c.Name, r.Text) || TextMatch.Key(c.Name) == key).ToList();
            if (exactNamed.Count > 0)
            {
                foreach (var c in exactNamed) if (Emit(c, 0.95, false) is { } ec) yield return ec;
                yield break;
            }

            foreach (var c in WellKnownContainers(wk, scope)) if (Emit(c, 1, true) is { } wc) yield return wc;
            yield break;
        }

        // "the Gmail Demo folder": a word naming an account narrows the scope and leaves the folder's own words.
        if (scope is null && TextMatch.Words(r.Norm ?? r.Text).Length >= 2)
        {
            var words = TextMatch.Words(r.Norm ?? r.Text).ToList();
            foreach (var account in _index.Accounts)
            {
                var accountWords = TextMatch.Words(account.Name).Concat(TextMatch.Words(TextMatch.Domain(account.Address)).Take(1)).Append(TextMatch.Fold(account.Account.ProviderType.ToString()))
                    .Concat(_brain.Find(BrainEntryKind.AccountNickname).Where(e => e.Subject.AccountId == account.Id).SelectMany(e => TextMatch.Words(e.Value))).ToHashSet();
                var rest = words.Where(w => !accountWords.Contains(w)).ToList();
                if (rest.Count == 0 || rest.Count == words.Count) continue;
                var narrowed = r with { Norm = string.Join(' ', rest), Text = string.Join(' ', rest) };
                var any = false;
                foreach (var c in ContainerCandidates(narrowed, kind, [account.Id]).Where(c => c.Score >= 0.7))
                {
                    any = true;
                    if (seen.Add(c.Ref.Key + c.Score)) yield return c;
                }

                if (any) yield break;
            }
        }

        foreach (var syn in _brain.Find(BrainEntryKind.ContainerSynonym).Where(e => ConceptMatches(e.Value, r) || e.SaidAs.Any(s => TextMatch.Same(s, r.Text))))
        {
            var c = _index.Containers.FirstOrDefault(x => x.Id == syn.Subject.ContainerId && x.AccountId == syn.Subject.AccountId);
            if (c is not null && kinds.Contains(c.Kind) && (scope is null || scope.Contains(c.AccountId)) && Emit(c, 1, true) is { } sc) yield return sc;
        }

        foreach (var k2 in kinds)
        {
            if (_brain.FindPick(key, k2) is { } pick && ItemRef.Parse(pick.Value) is { } pr)
            {
                var c = _index.Containers.FirstOrDefault(x => x.Ref.Key == pr.Key);
                if (c is not null && (scope is null || scope.Contains(c.AccountId)) && Emit(c, 0.95, true) is { } pc) yield return pc;
            }
        }

        if (norm.Length == 0 && text.Length == 0) yield break;
        foreach (var k2 in kinds)
        {
            foreach (var c in _index.ContainersOf(k2, scope))
            {
                var name = TextMatch.Fold(c.Name);
                if (name.Length == 0) continue;
                if (name == norm || name == text || TextMatch.Key(c.Name) == key)
                {
                    if (Emit(c, 0.95, false) is { } ec) yield return ec;
                }
                else if ((norm.Length >= 3 && name.Contains(norm)) || (text.Length >= 3 && name.Contains(text)) || TextMatch.NameScore(TextMatch.Words(r.Norm ?? r.Text), TextMatch.Words(c.Name)) > 0)
                {
                    if (Emit(c, 0.7, false) is { } cc) yield return cc;
                }
                else if (k2 == ItemKind.Calendar && c.IsPrimary && _index.Account(c.AccountId) is { } account
                    && (TextMatch.Same(account.Name, r.Norm) || TextMatch.Same(account.Name, r.Text)
                        || TextMatch.Words(r.Norm ?? r.Text).Any(w => w == TextMatch.Fold(account.Account.ProviderType.ToString()) || TextMatch.Words(TextMatch.Domain(account.Address)).Contains(w))
                        || _brain.Find(BrainEntryKind.AccountNickname).Any(e => e.Subject.AccountId == account.Id && (TextMatch.Same(e.Value, r.Norm) || TextMatch.Same(e.Value, r.Text)))))
                {
                    // "my Gmail calendar", "the Personal calendar": that account's primary calendar.
                    if (Emit(c, 0.8, false) is { } pc) yield return pc;
                }
            }
        }
    }

    private IEnumerable<DomainIndex.Container> WellKnownContainers(string wellKnown, IReadOnlyList<Guid>? scope)
    {
        if (DomainIndex.SpecialOf(wellKnown) is { } special) return _index.SpecialFolders(special, scope);
        return wellKnown switch
        {
            AssistantWellKnown.PrimaryCalendar => _index.ContainersOf(ItemKind.Calendar, scope).Where(c => c.IsPrimary),
            AssistantWellKnown.DefaultTaskList => _index.ContainersOf(ItemKind.TaskList, scope).Where(c => c.IsDefault),
            _ => [],
        };
    }

    /// <summary>
    /// Resolves the container refs of an argument to container ids. Several exact matches across accounts (an
    /// "Archive" in each) are all taken for a selection; a destination or a folder action asks. Nothing matching asks
    /// with every container of the type to pick from; the pick is saved as a synonym (a description) or a pick (a name).
    /// </summary>
    private async Task<List<DomainIndex.Container>> ResolveContainersAsync(AssistantStep step, AgentAction action, string argName, AccountScope scope, bool single, CancellationToken ct)
    {
        var result = new List<DomainIndex.Container>();
        var i = 0;
        foreach (var r in Refs(step, argName))
        {
            if (_asAccount.Contains(r.Id)) continue;
            var field = i++ == 0 ? argName : $"{argName}[{i - 1}]";
            var kind = ActionCatalog.ContainerKindOf(action, argName);
            if (r.Kind == AssistantRefKinds.Unknown)
            {
                var picked = await ResolveUnknownAsync(step, r, field, ct).ConfigureAwait(false);
                if (picked is not null && _index.Containers.FirstOrDefault(c => c.Ref.Key == picked.Key) is { } pc) result.Add(pc);
                continue;
            }

            if (r.Kind == AssistantRefKinds.Account)
            {
                // "the Gmail calendar" read as an account: its primary container.
                var account = await ResolveAccountAsync(step, r, field, ct).ConfigureAwait(false);
                if (account is not null && _index.Primary(kind, account.Id) is { } primary) result.Add(primary);
                continue;
            }

            var candidates = ContainerCandidates(r, kind, scope.Ids).ToList();
            var (decision, top, ranked) = Ranking.Decide(candidates, 0.9, 0.2);
            if (decision == Decision.Resolved)
            {
                result.Add(Find(top!.Ref)!);
                continue;
            }

            if (decision == Decision.Ask && !single && r.Props?.WellKnown is not null)
            {
                // A well-known folder of every account in scope ("my sent mails"): all of them, no question.
                result.AddRange(ranked.Select(c => Find(c.Ref)!).Where(c => c is not null));
                continue;
            }

            if (decision == Decision.Ask && kind is ItemKind.Category or ItemKind.ContactList or ItemKind.TaskList && ranked.Count(c => c.Score >= 0.7) == 1
                && (scope.One is not null || _t.Request.Selection?.AccountId is not null) && Find(ranked.First(c => c.Score >= 0.7).Ref) is { } only && (scope.One ?? _t.Request.Selection!.AccountId) == only.AccountId)
            {
                // "Yellow" with one "Yellow category" in the account at hand: the only partial match of a category or list resolves.
                result.Add(only);
                continue;
            }

            if (decision == Decision.Ask && _t.Request.Selection?.AccountId is { } selectedAccount && ranked.Where(c => c.Score >= 0.95 || c.Exact).Select(c => Find(c.Ref)).OfType<DomainIndex.Container>().Where(c => c.AccountId == selectedAccount).ToList() is { Count: 1 } ofSelection)
            {
                // The same name in several accounts: the selected items' account decides ("put it in Yellow category").
                result.Add(ofSelection[0]);
                continue;
            }

            if (decision == Decision.Ask && !single && ranked.All(c => c.Score >= 0.95 || c.Exact) && ranked.Select(c => TextMatch.Fold(c.Title)).Distinct().Count() == 1)
            {
                // The same name in several accounts, as a selection: all of them.
                result.AddRange(ranked.Select(c => Find(c.Ref)!).Where(c => c is not null));
                continue;
            }

            var options = ranked.Count > 0 ? ranked : _index.ContainersOf(kind, scope.Ids).Select(c => ContainerToCandidate(c, 0, false)).ToList();
            if (options.Count == 0) throw new TurnMessageException(AgentMessageKind.Unavailable, SaidAs(r));
            var answer = Consume(step.N, field, ClarificationKind.PickContainer, SaidAs(r), options, searchable: true, suggested: ranked.FirstOrDefault(), filter: r.Norm ?? r.Text);
            var chosen = answer.Refs.Select(Find).OfType<DomainIndex.Container>().ToList();
            if (chosen.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, SaidAs(r));
            foreach (var c in chosen)
            {
                if (r.By == AssistantRefBy.Description && TextMatch.IsConcept(r.Norm))
                    Learn(BrainEntryKind.ContainerSynonym, BrainSubject.Container(c.Kind, c.AccountId, c.Id, c.Name), TextMatch.Fold(r.Norm), BrainSource.Pick, r.Text);
                else if (TextMatch.Key(r.Norm ?? r.Text) is { Length: > 0 } key)
                    Learn(BrainEntryKind.Pick, BrainSubject.Words(key, c.Kind), c.Ref.Key, BrainSource.Pick, r.Text);
            }

            result.AddRange(chosen);
        }

        return result;
    }

    private DomainIndex.Container? Find(ItemRef r) => _index.Containers.FirstOrDefault(c => c.Ref.Key == r.Key);

    /// <summary>The calendar for a new event: named, else the stated default, else the account's primary (the selected items' account when the request is about a selection), else the only writable one, else a question.</summary>
    private async Task<DomainIndex.Container?> ResolveCalendarForNewEventAsync(AssistantStep step, AccountScope scope, CancellationToken ct)
    {
        var named = await ResolveContainersAsync(step, AgentAction.CreateEvent, "calendar", scope, true, ct).ConfigureAwait(false);
        if (named.Count > 0) return named[0];
        if (_brain.Find(BrainEntryKind.Default).FirstOrDefault(e => e.Value.StartsWith("calendar=", StringComparison.Ordinal)) is { } d && ItemRef.Parse(d.Value[9..]) is { } dr && Find(dr) is { } dc && (scope.IsAll || scope.Ids!.Contains(dc.AccountId)))
            return dc;
        if (scope.One is { } one && _index.Primary(ItemKind.Calendar, one) is { } primary) return primary;
        if (scope.IsAll && _t.Request.Selection?.AccountId is { } selected && _index.Primary(ItemKind.Calendar, selected) is { ReadOnly: false } selectedPrimary) return selectedPrimary;
        var writable = _index.ContainersOf(ItemKind.Calendar, scope.Ids).Where(c => !c.ReadOnly).ToList();
        if (writable.Count == 1) return writable[0];
        // Every writable calendar is one account's: its primary, as when that account was named.
        if (writable.Select(c => c.AccountId).Distinct().ToList() is [var onlyAccount] && _index.Primary(ItemKind.Calendar, onlyAccount) is { ReadOnly: false } onlyPrimary) return onlyPrimary;
        if (scope.IsAll && _t.Request.CurrentAccountId is { } cur && _index.Primary(ItemKind.Calendar, cur) is { } curPrimary) return curPrimary;
        if (writable.Count == 0) throw new TurnMessageException(AgentMessageKind.Unavailable, "calendar");
        var answer = Consume(step.N, "calendar", ClarificationKind.PickContainer, step.Gloss ?? "calendar", writable.Select(c => ContainerToCandidate(c, c.IsPrimary ? 0.5 : 0, false)).ToList(), searchable: true);
        var chosen = answer.Refs.Select(Find).OfType<DomainIndex.Container>().FirstOrDefault();
        if (chosen is null) throw new TurnMessageException(AgentMessageKind.NothingMatched, "calendar");
        _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.Default, BrainSubject.Self(), "calendar=" + chosen.Ref.Key, chosen.Name));
        return chosen;
    }

    /// <summary>The list for a new task: named, else the stated default list, else the default task account's default list, else the only list, else a question.</summary>
    private async Task<DomainIndex.Container?> ResolveListForNewTaskAsync(AssistantStep step, AccountScope scope, CancellationToken ct)
    {
        var named = await ResolveContainersAsync(step, AgentAction.CreateTask, "list", scope, true, ct).ConfigureAwait(false);
        if (named.Count > 0) return named[0];
        if (_brain.Find(BrainEntryKind.Default).FirstOrDefault(e => e.Value.StartsWith("taskList=", StringComparison.Ordinal)) is { } d && ItemRef.Parse(d.Value[9..]) is { } dr && Find(dr) is { } dl && (scope.IsAll || scope.Ids!.Contains(dl.AccountId)))
            return dl;
        var account = scope.One is { } one ? _index.Account(one) : DefaultAccount(step, "task", step.Gloss ?? "task");
        if (account is not null)
        {
            var lists = _index.ContainersOf(ItemKind.TaskList, [account.Id]).Where(l => !l.ReadOnly).ToList();
            var pick = lists.FirstOrDefault(l => l.IsDefault) ?? (lists.Count == 1 ? lists[0] : null);
            if (pick is not null) return pick;
            // No list there: the account came from the selection or the defaults, so another account may hold the task.
            if (lists.Count == 0 && (scope.One is null || scope.One == _t.Request.Selection?.AccountId)) account = null;
        }

        var all = _index.ContainersOf(ItemKind.TaskList, account is null ? null : [account.Id]).Where(l => !l.ReadOnly).ToList();
        if (all.Count == 1) return all[0];
        if (all.Count == 0) throw new TurnMessageException(AgentMessageKind.Unavailable, "task list");
        var answer = Consume(step.N, "list", ClarificationKind.PickContainer, step.Gloss ?? "list", all.Select(c => ContainerToCandidate(c, c.IsDefault ? 0.5 : 0, false)).ToList(), searchable: true);
        var chosen = answer.Refs.Select(Find).OfType<DomainIndex.Container>().FirstOrDefault();
        if (chosen is null) throw new TurnMessageException(AgentMessageKind.NothingMatched, "list");
        _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.Default, BrainSubject.Self(), "taskList=" + chosen.Ref.Key, chosen.Name));
        return chosen;
    }

    private IReadOnlyList<Guid> SpecialFolderIds(SpecialFolderType special, AccountScope scope) => _index.SpecialFolders(special, scope.Ids).Select(c => c.Id).ToList();
}
