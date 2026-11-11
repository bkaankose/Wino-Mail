using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Session;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

public sealed partial class TurnResolver
{
    /// <summary>
    /// How a person ref is used: as someone to write to (a question when unknown), as a filter on existing items (the words
    /// stand in when unknown), or as the sender of a mail rule (a filter that acts on future mail for good: a person nobody
    /// matches asks, while a label, a pronoun or an org resolve as for a filter).
    /// </summary>
    private enum PersonUse
    {
        Recipient,
        Filter,
        RuleSender,
    }

    private sealed record PeopleResult(List<Recipient> People, string? Words)
    {
        public static readonly PeopleResult Empty = new([], null);
    }

    /// <summary>Resolves every person and group ref of an argument, in order, one question at a time.</summary>
    private async Task<PeopleResult> ResolvePeopleAsync(AssistantStep step, string argName, PersonUse use, CancellationToken ct, IEnumerable<AssistantRef>? refs = null, Guid? accountId = null)
    {
        var people = new List<Recipient>();
        var words = new List<string>();
        var i = 0;
        foreach (var r in refs ?? Refs(step, argName))
        {
            foreach (var member in Expand(r))
            {
                var field = $"{argName}[{i++}]";
                var one = await ResolvePersonAsync(step, member, field, use, ct, accountId).ConfigureAwait(false);
                people.AddRange(one.People.Where(p => !people.Any(x => string.Equals(x.Address, p.Address, StringComparison.OrdinalIgnoreCase))));
                if (one.Words is not null) words.Add(one.Words);
            }
        }

        var role = argName switch { "to" or "cc" or "bcc" or "attendees" or "attendee" or "person" or "sender" or "recipient" => argName, _ => "to" };
        AddPeople(role, people);
        return new PeopleResult(people, words.Count == 0 ? null : string.Join(" ", words));
    }

    private async Task<PeopleResult> ResolvePersonAsync(AssistantStep step, AssistantRef r, string field, PersonUse use, CancellationToken ct, Guid? accountId)
    {
        // The people of the selection: "the sender", "everyone on these mails", "her" with a contact selected. Whatever the
        // classifier made of the words (a pronoun, a description, a set with no members), the selection answers it.
        if (SelectionPeople(r) is { Count: > 0 } selected) return new(selected, null);
        // A pronoun: "I" is the user; "who" asked about an item's people filters nothing; "him" as a recipient binds to the conversation.
        if (r.By == AssistantRefBy.Pronoun && r.Kind != AssistantRefKinds.Self)
        {
            // A rule's sender pronoun with nothing to bind to is a message, never an empty filter (the rule would match every mail).
            if (use == PersonUse.Filter) return ResolvePronoun(step, r, field, throwWhenUnbound: false);
            return ResolvePronoun(step, r, field);
        }

        if (r.Kind == AssistantRefKinds.Group || r.By == AssistantRefBy.Set) return await ResolveGroupAsync(step, r, field, use, ct).ConfigureAwait(false);
        if (r.Kind == AssistantRefKinds.Self || r.By == AssistantRefBy.Self)
        {
            var account = _index.Account(accountId) ?? _index.Account(_t.Request.CurrentAccountId) ?? _index.Accounts.FirstOrDefault();
            return account is null ? PeopleResult.Empty : new([new Recipient(account.Address, account.Account.SenderName)], null);
        }

        if (r.Kind == AssistantRefKinds.Org) return await ResolveOrgAsync(step, r, field, use, ct).ConfigureAwait(false);
        if (r.Kind == AssistantRefKinds.Unknown)
        {
            var picked = await ResolveUnknownAsync(step, r, field, ct).ConfigureAwait(false);
            if (picked is { Kind: ItemKind.Contact } c) return new([new Recipient(c.Id, null)], null);
            if (use == PersonUse.RuleSender) return await ResolveByNameAsync(step, r, field, use, ct).ConfigureAwait(false);
            return new([], r.Norm ?? r.Text);
        }

        return r.By switch
        {
            AssistantRefBy.Relationship => await ResolveRelationshipAsync(step, r, field, ct).ConfigureAwait(false),
            AssistantRefBy.Pronoun => ResolvePronoun(step, r, field),
            _ => await ResolveByNameAsync(step, r, field, use, ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// The people of the selection the app resolved (<see cref="AgentSelection.Senders"/>) when the ref points at them: a
    /// pronoun no earlier step of this turn bound, a ref named by a step-link part word (sender, recipients, organizer,
    /// attendees, people), a set with no members, or a sender-role ref that is no name. Null when the selection cannot answer.
    /// </summary>
    private List<Recipient>? SelectionPeople(AssistantRef r)
    {
        if (_t.Request.Selection is not { Senders.Count: > 0 } sel) return null;
        if (r.Kind is AssistantRefKinds.Org or AssistantRefKinds.Account or AssistantRefKinds.Container or AssistantRefKinds.Time or AssistantRefKinds.Content) return null;
        var words = TextMatch.Words(r.Norm ?? r.Text);
        var partWord = words.Length > 0 && words.Length <= 3 && words.Any(w => AssistantStepLinkParts.All.Any(p => w == p || (w.StartsWith(p, StringComparison.Ordinal) && w.Length <= p.Length + 2)));
        var pointsAtSelection = partWord
            || (r.By == AssistantRefBy.Pronoun && !_t.People.Any(kv => kv.Key is "to" or "cc" or "attendees" or "attendee" or "person" && kv.Value.Count > 0))
            || (r.Kind == AssistantRefKinds.Group && r.By == AssistantRefBy.Set && r.Members is not { Count: > 0 })
            || (r.Role == AssistantRoles.Sender && r.By is AssistantRefBy.Description or AssistantRefBy.Relationship && TextMatch.Same(r.Norm, AssistantRoles.Sender));
        return pointsAtSelection ? sel.Senders.DistinctBy(p => p.Address.ToLowerInvariant()).ToList() : null;
    }

    private static Recipient ToRecipient(Candidate c) => new(c.Ref.Id, c.Title == c.Ref.Id ? null : c.Title);

    private static Candidate ContactCandidate(string address, string? name, double score, bool exact)
        => new(ItemRef.Contact(address), string.IsNullOrWhiteSpace(name) ? address : name!, string.IsNullOrWhiteSpace(name) ? null : address, null, score, exact);

    /// <summary>Name candidates: a stated alias, a pick, then contacts whose words the name words start (plan 4.4 person/name).</summary>
    private async Task<List<Candidate>> NameCandidatesAsync(AssistantRef r, CancellationToken ct)
    {
        var candidates = new List<Candidate>();
        var query = r.Norm ?? r.Text ?? "";
        var words = TextMatch.Words(query);
        if (words.Length == 0) words = TextMatch.Words(r.Text);
        var key = string.Concat(words);
        if (TextMatch.LooksLikeAddress(r.Norm) || TextMatch.LooksLikeAddress(r.Text))
        {
            var address = (TextMatch.LooksLikeAddress(r.Norm) ? r.Norm : r.Text)!.Trim();
            candidates.Add(ContactCandidate(address, null, 1, true));
        }

        foreach (var alias in _brain.Find(BrainEntryKind.PersonAlias).Where(e => e.Subject.Address is not null && (TextMatch.Key(e.Value) == key || (e.Value.Contains(' ') && TextMatch.Words(e.Value).SequenceEqual(words)))))
            candidates.Add(ContactCandidate(alias.Subject.Address!, alias.Subject.Name, 1, true));
        if (_brain.FindPick(key, ItemKind.Contact) is { } pick && ItemRef.Parse(pick.Value) is { Kind: ItemKind.Contact } pr && !candidates.Any(c => c.Exact))
            candidates.Add(ContactCandidate(pr.Id, pick.Subject.Name, 0.95, true));

        if (words.Length > 0)
        {
            var contacts = await _q.SearchContactsAsync(string.Join(' ', words), Ranking.MaxOptions, ct).ConfigureAwait(false);
            var scored = new List<(Candidate C, bool FirstNameOnly)>();
            foreach (var c in contacts)
            {
                if (string.IsNullOrWhiteSpace(c.Address)) continue;
                var nameWords = TextMatch.Words(c.Name);
                var localWords = TextMatch.Words(TextMatch.LocalPart(c.Address));
                var score = Math.Max(TextMatch.NameScore(words, nameWords), TextMatch.NameScore(words, localWords));
                if (score == 0 && r.By == AssistantRefBy.Description && r.Props?.Domain is { } domain && TextMatch.Domain(c.Address) == TextMatch.Fold(domain))
                    score = 0.6;
                if (score == 0) continue;
                var exact = score >= 1;
                if (!exact && c.IsTopCorrespondent) score += 0.2;
                if (!exact && r.Props?.Domain is { } d2 && TextMatch.Domain(c.Address).Contains(TextMatch.Fold(d2))) score += 0.2;
                scored.Add((ContactCandidate(c.Address, c.Name, exact ? 1 : Math.Min(score, 0.99), exact), words.Length == 1 && !exact && nameWords.Length > 0 && nameWords[0] == words[0]));
            }

            // A first name alone naming exactly one contact with that first name resolves (0.85).
            if (words.Length == 1 && scored.Count(s => s.FirstNameOnly) == 1 && scored.Count == 1)
            {
                var only = scored[0].C;
                scored[0] = (only with { Score = Math.Max(only.Score, 0.85) }, true);
            }

            candidates.AddRange(scored.Select(s => s.C));
        }

        return candidates;
    }

    private async Task<PeopleResult> ResolveByNameAsync(AssistantStep step, AssistantRef r, string field, PersonUse use, CancellationToken ct)
    {
        // "my insurance", "the gym" read as a person by description: a stated sender label for the concept answers it (an address for anyone, a domain as a filter).
        if (r.By == AssistantRefBy.Description && TextMatch.IsConcept(r.Norm))
        {
            var labels = _brain.Find(BrainEntryKind.SenderLabel).Where(e => e.Subject.Address is not null && ConceptMatches(e.Value, r) && (use != PersonUse.Recipient || TextMatch.LooksLikeAddress(e.Subject.Address))).ToList();
            if (labels.Count == 1) return new([new Recipient(labels[0].Subject.Address!, labels[0].Subject.Name)], null);
        }

        var candidates = await NameCandidatesAsync(r, ct).ConfigureAwait(false);
        // A pick or a stated alias beats ranking; two exact entries that disagree ask.
        var exact = candidates.Where(c => c.Exact).GroupBy(c => c.Ref.Key).ToList();
        if (exact.Count == 1)
        {
            var only = exact[0].OrderByDescending(c => c.Score).First();
            if (r.By == AssistantRefBy.Description && r.Props?.Topic is { } topic0 && TextMatch.IsConcept(topic0) && !TextMatch.Quoted(only.Title, topic0))
                _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.PersonRelation, BrainSubject.Contact(only.Ref.Id, only.Title), TextMatch.Fold(topic0), SaidAs(r)));
            return new([ToRecipient(only)], null);
        }

        var (decision, top, ranked) = Ranking.Decide(candidates, 0.8, 0.25);
        if (decision == Decision.Resolved)
        {
            if (!top!.Exact && top.Score < 0.999)
            {
                // A ranked guess: offered as an alias after the plan ran (plan 4.8, inferred).
                var key = TextMatch.Key(r.Norm ?? r.Text);
                if (key.Length >= 2) _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.PersonAlias, BrainSubject.Contact(top.Ref.Id, top.Title), key, SaidAs(r)));
            }
            else if (r.By == AssistantRefBy.Description && r.Props?.Topic is { } topic && TextMatch.IsConcept(topic) && !TextMatch.Quoted(top.Title, topic))
            {
                // "my doctor Sandy Pohlman": the description's concept is an inferred relation, offered after the plan ran.
                _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.PersonRelation, BrainSubject.Contact(top.Ref.Id, top.Title), TextMatch.Fold(topic), SaidAs(r)));
            }

            return new([ToRecipient(top!)], null);
        }

        // A filter on existing mail may stand in the words; a rule's sender may not (the rule would catch every similar name for good): it asks, with a new address allowed.
        if (decision == Decision.None && use == PersonUse.Filter) return new([], r.Norm ?? r.Text);
        var answer = Consume(step.N, field, ClarificationKind.PickPerson, SaidAs(r), ranked, searchable: true, newAddress: true, suggested: ranked.FirstOrDefault(), filter: r.Norm ?? r.Text);
        return LearnPersonPick(r, answer, BrainEntryKind.Pick);
    }

    /// <summary>Writes the pick: a <c>Pick</c> for a name, a <c>PersonRelation</c> for a relationship question.</summary>
    private PeopleResult LearnPersonPick(AssistantRef r, Consumed answer, BrainEntryKind kind, string? relation = null)
    {
        var people = answer.Chosen.Where(c => c.Ref.Kind == ItemKind.Contact).Select(ToRecipient).ToList();
        if (answer.NewAddress is { } address && TextMatch.LooksLikeAddress(address) && people.Count == 0) people.Add(new Recipient(address.Trim(), null));
        if (people.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, SaidAs(r));
        var key = TextMatch.Key(r.Norm ?? r.Text);
        foreach (var p in people)
        {
            if (kind == BrainEntryKind.PersonRelation && relation is not null)
                Learn(BrainEntryKind.PersonRelation, BrainSubject.Contact(p.Address, p.Name), relation, BrainSource.Pick, r.Text);
            else if (key.Length > 0)
                Learn(BrainEntryKind.Pick, BrainSubject.Words(key, ItemKind.Contact), ItemRef.Contact(p.Address).Key, BrainSource.Pick, r.Text);
        }

        return new(people, null);
    }

    /// <summary>A relationship ("my father") comes only from the brain; none or several ask, and the pick is saved as the relation.</summary>
    private Task<PeopleResult> ResolveRelationshipAsync(AssistantStep step, AssistantRef r, string field, CancellationToken ct)
    {
        var concept = TextMatch.Fold(r.Norm);
        var facts = _brain.Find(BrainEntryKind.PersonRelation, concept).Where(e => e.Subject.Address is not null).ToList();
        if (facts.Count == 0) facts = _brain.Find(BrainEntryKind.PersonRelation).Where(e => e.Subject.Address is not null && ConceptMatches(e.Value, r)).ToList();
        if (facts.Count == 0)
        {
            // "my airline", "my bank" read as a relationship: a stated sender label answers it.
            var labels = _brain.Find(BrainEntryKind.SenderLabel).Where(e => e.Subject.Address is not null && ConceptMatches(e.Value, r)).ToList();
            if (labels.Count == 1) return Task.FromResult(new PeopleResult([new Recipient(labels[0].Subject.Address!, labels[0].Subject.Name)], null));
        }

        if (facts.Count == 1) return Task.FromResult(new PeopleResult([new Recipient(facts[0].Subject.Address!, facts[0].Subject.Name)], null));
        if (facts.Count == 0)
        {
            // "my doctor Sandy Pohlman": the words carry the name. The classifier puts the name in norm or topic and the
            // concept in the other; whichever side names exactly one contact is the name, the other side is the concept.
            foreach (var (nameSource, conceptSource) in new[] { (r.Props?.Topic, r.Norm), (r.Norm, r.Props?.Topic), (r.Text, r.Norm) })
            {
                var conceptCandidate = TextMatch.Fold(conceptSource);
                if (string.IsNullOrWhiteSpace(nameSource) || !TextMatch.IsConcept(conceptCandidate)) continue;
                var exactNamed = NameCandidatesAsync(r with { By = AssistantRefBy.Name, Norm = nameSource, Text = nameSource }, ct).GetAwaiter().GetResult().Where(c => c.Score >= 0.999).DistinctBy(c => c.Ref.Key).ToList();
                if (exactNamed.Count != 1 || TextMatch.Key(exactNamed[0].Title) == TextMatch.Key(conceptCandidate) || TextMatch.Quoted(exactNamed[0].Title, conceptCandidate)) continue;
                Learn(BrainEntryKind.PersonRelation, BrainSubject.Contact(exactNamed[0].Ref.Id, exactNamed[0].Title), conceptCandidate, BrainSource.Pick, r.Text);
                return Task.FromResult(new PeopleResult([ToRecipient(exactNamed[0])], null));
            }
        }

        var options = facts.Select(f => ContactCandidate(f.Subject.Address!, f.Subject.Name, 1, true)).ToList();
        var answer = Consume(step.N, field, ClarificationKind.PickPerson, SaidAs(r), options, searchable: facts.Count == 0, newAddress: facts.Count == 0, filter: facts.Count == 0 ? r.Norm : null);
        return Task.FromResult(LearnPersonPick(r, answer, BrainEntryKind.PersonRelation, concept));
    }

    /// <summary>"them", "him": the last turn's people of the same role, else the selection's senders; several ask with those first.</summary>
    private PeopleResult ResolvePronoun(AssistantStep step, AssistantRef r, string field, bool throwWhenUnbound = true)
    {
        // People this turn already named ("forward it to Christian and tell him..."), then the previous turns', then the selection's.
        var bound = (_t.People.TryGetValue(r.Role ?? "to", out var sameRole) && sameRole.Count > 0 ? sameRole : _t.People.Where(kv => kv.Key != AssistantRoles.Sender).SelectMany(kv => kv.Value).DistinctBy(p => p.Address.ToLowerInvariant()).ToList()).ToList();
        if (bound.Count == 0) bound = _ctx.LastPeople(Now, r.Role).ToList();
        if (bound.Count == 0 && _t.Request.Selection is { Senders.Count: > 0 } sel) bound = sel.Senders.ToList();
        if (bound.Count == 1) return new(bound, null);
        if (bound.Count == 0 && !throwWhenUnbound) return PeopleResult.Empty;
        if (bound.Count == 0) throw new TurnMessageException(AgentMessageKind.NeedsPreviousRequest, SaidAs(r));
        var options = bound.Select(b => ContactCandidate(b.Address, b.Name, 0.5, false)).ToList();
        var answer = Consume(step.N, field, ClarificationKind.PickPerson, SaidAs(r), options, many: true, searchable: true, newAddress: false);
        var people = answer.Chosen.Where(c => c.Ref.Kind == ItemKind.Contact).Select(ToRecipient).ToList();
        return new(people, null);
    }

    /// <summary>A group by name comes from the brain; a set resolves member by member.</summary>
    private async Task<PeopleResult> ResolveGroupAsync(AssistantStep step, AssistantRef r, string field, PersonUse use, CancellationToken ct)
    {
        // A group named by its concept ("the family") is the brain's group; members the classifier listed beside the name
        // (copied from a previous turn) do not narrow it.
        if (r.By == AssistantRefBy.Name && _brain.Find(BrainEntryKind.Group).FirstOrDefault(g => ConceptMatches(g.Subject.Text, r)) is { } named)
        {
            var namedMembers = named.Members.Select(m => ItemRef.Parse(m)).OfType<ItemRef>().Select(m => new Recipient(m.Id, MemberName(m.Id))).ToList();
            if (namedMembers.Count > 0) return new(namedMembers, null);
        }

        if (r.Members is { Count: > 0 })
        {
            var all = new List<Recipient>();
            var words = new List<string>();
            var i = 0;
            foreach (var id in r.Members)
            {
                if (Ref(id) is not { } member) continue;
                var one = await ResolvePersonAsync(step, member, $"{field}.{i++}", use, ct, null).ConfigureAwait(false);
                all.AddRange(one.People);
                if (one.Words is not null) words.Add(one.Words);
            }

            return new(all, words.Count == 0 ? null : string.Join(" ", words));
        }

        var concept = TextMatch.Fold(r.Norm);
        var group = _brain.Find(BrainEntryKind.Group).FirstOrDefault(g => ConceptMatches(g.Subject.Text, r));
        if (group is not null)
        {
            var members = group.Members.Select(m => ItemRef.Parse(m)).OfType<ItemRef>().Select(m => new Recipient(m.Id, MemberName(m.Id))).ToList();
            if (members.Count > 0) return new(members, null);
        }

        var top = await _q.SearchContactsAsync("", Ranking.MaxOptions, ct).ConfigureAwait(false);
        var options = top.Where(c => !string.IsNullOrWhiteSpace(c.Address)).Select(c => ContactCandidate(c.Address, c.Name, 0, false)).ToList();
        var answer = Consume(step.N, field, ClarificationKind.PickPeople, SaidAs(r), options, many: true, searchable: true, newAddress: true);
        var people = answer.Chosen.Where(c => c.Ref.Kind == ItemKind.Contact).Select(ToRecipient).ToList();
        if (answer.NewAddress is { } address && TextMatch.LooksLikeAddress(address)) people.Add(new Recipient(address.Trim(), null));
        if (people.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, SaidAs(r));
        if (TextMatch.IsConcept(concept)) Learn(BrainEntryKind.Group, BrainSubject.Concept(concept), string.Join(';', people.Select(p => ItemRef.Contact(p.Address).Key)), BrainSource.Pick, r.Text);
        return new(people, null);
    }

    /// <summary>The display name the brain knows for a contact address, if any.</summary>
    private string? MemberName(string address)
        => _brain.Entries.FirstOrDefault(e => e.Subject.Type == BrainSubjectType.Contact && string.Equals(e.Subject.Address, address, StringComparison.OrdinalIgnoreCase) && e.Subject.Name is not null)?.Subject.Name;

    // ---- organizations (senders) ----------------------------------------------------------------------------------

    private sealed record OrgResult(List<string> Addresses, List<string> Domains, string? Words);

    /// <summary>An org as a sender filter (plan 4.4 org row): a stated label, a pick, a domain, the sender index; a brand nothing matches becomes words, a description asks.</summary>
    private async Task<OrgResult> ResolveOrgFilterAsync(AssistantStep step, AssistantRef r, string field, CancellationToken ct)
    {
        var norm = TextMatch.Fold(r.Norm);
        var text = TextMatch.Fold(r.Text);
        var key = TextMatch.Key(r.Norm ?? r.Text);
        var candidates = new List<Candidate>();
        Candidate S(string addressOrDomain, string? name, double score, bool exact) => new(ItemRef.Sender(addressOrDomain), name ?? addressOrDomain, name is null ? null : addressOrDomain, null, score, exact);
        foreach (var label in _brain.Find(BrainEntryKind.SenderLabel).Where(e => e.Subject.Address is not null && (ConceptMatches(e.Value, r) || e.SaidAs.Any(s => TextMatch.Same(s, r.Text)))))
            candidates.Add(S(label.Subject.Address!, label.Subject.Name, 1, true));
        if (_brain.FindPick(key, ItemKind.Sender) is { } pick && ItemRef.Parse(pick.Value) is { } pr && !candidates.Any(c => c.Exact))
            candidates.Add(S(pr.Id, pick.Subject.Name, 0.95, true));
        if (TextMatch.LooksLikeAddress(r.Norm)) candidates.Add(S(r.Norm!.Trim(), null, 1, true));
        else if (TextMatch.LooksLikeDomain(r.Norm)) candidates.Add(S(norm, null, 0.95, true));
        else if (r.Props?.Domain is { } d && TextMatch.LooksLikeDomain(d)) candidates.Add(S(TextMatch.Fold(d), null, 0.95, true));
        if (candidates.Count == 0 && norm.Length >= 2)
        {
            foreach (var s in await _q.SearchSendersAsync(r.Norm ?? r.Text ?? "", 20, ct).ConfigureAwait(false))
            {
                var domainLabel = TextMatch.Words(s.Domain).FirstOrDefault() ?? "";
                if (domainLabel == norm || TextMatch.Fold(s.Domain) == norm) candidates.Add(S(s.Domain, s.Name, 0.95, false));
                else if (TextMatch.Contains(s.Name, norm) || TextMatch.Contains(s.Domain, norm) || TextMatch.Contains(s.Address, norm)) candidates.Add(S(s.Address, s.Name, 0.7, false));
            }
        }

        var (decision, top, ranked) = Ranking.Decide(candidates, 0.7, 0.3);
        if (decision == Decision.Resolved) return ToOrg(top!);
        if (decision == Decision.None && r.By != AssistantRefBy.Description) return new([], [], r.Norm ?? r.Text);
        var answer = Consume(step.N, field, ClarificationKind.PickSender, SaidAs(r), ranked, searchable: true, newAddress: true, filter: r.Norm ?? r.Text);
        var chosen = answer.Chosen.FirstOrDefault(c => c.Ref.Kind == ItemKind.Sender) ?? (answer.NewAddress is { } na ? S(na.Trim(), null, 1, true) : null);
        if (chosen is null) return new([], [], r.Norm ?? r.Text);
        if (r.By == AssistantRefBy.Description && TextMatch.IsConcept(r.Norm))
            Learn(BrainEntryKind.SenderLabel, BrainSubject.Org(chosen.Ref.Id, chosen.Title == chosen.Ref.Id ? null : chosen.Title), norm, BrainSource.Pick, r.Text);
        else if (key.Length > 0)
            Learn(BrainEntryKind.Pick, BrainSubject.Words(key, ItemKind.Sender), chosen.Ref.Key, BrainSource.Pick, r.Text);
        return ToOrg(chosen);
    }

    private static OrgResult ToOrg(Candidate c) => TextMatch.LooksLikeAddress(c.Ref.Id) ? new([c.Ref.Id], [], null) : new([], [c.Ref.Id], null);

    /// <summary>An org where a person is expected (a recipient): the label's or pick's address when it is one; otherwise ask for a person.</summary>
    private async Task<PeopleResult> ResolveOrgAsync(AssistantStep step, AssistantRef r, string field, PersonUse use, CancellationToken ct)
    {
        var org = await ResolveOrgFilterAsync(step, r, field, ct).ConfigureAwait(false);
        if (org.Addresses.Count > 0) return new(org.Addresses.Select(a => new Recipient(a, null)).ToList(), null);
        if (use != PersonUse.Recipient) return new([], org.Words ?? r.Norm ?? r.Text);
        var byName = await ResolveByNameAsync(step, r, field, use, ct).ConfigureAwait(false);
        return byName;
    }

    // ---- unknown refs -------------------------------------------------------------------------------------------

    /// <summary>An unknown ref ("show Personal"): candidates from every kind it could be; one kind resolves alone, several ask <c>ChooseKind</c>; the pick is remembered.</summary>
    private async Task<ItemRef?> ResolveUnknownAsync(AssistantStep step, AssistantRef r, string field, CancellationToken ct)
    {
        var key = TextMatch.Key(r.Norm ?? r.Text);
        if (_brain.FindPick(key, null) is { } pick && ItemRef.Parse(pick.Value) is { } picked) return picked;
        var kinds = r.Kinds is { Count: > 0 } ? r.Kinds : [AssistantRefKinds.Account, AssistantRefKinds.Container, AssistantRefKinds.Person];
        var byKind = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (var kind in kinds)
        {
            var list = kind switch
            {
                AssistantRefKinds.Account => AccountCandidates(r),
                AssistantRefKinds.Container => ContainerCandidates(r, null, null).ToList(),
                AssistantRefKinds.Person => await NameCandidatesAsync(r, ct).ConfigureAwait(false),
                _ => [],
            };
            var strong = list.Where(c => c.Score >= 0.7).ToList();
            if (strong.Count > 0) byKind[kind] = strong;
        }

        if (byKind.Count == 0) return null;
        if (byKind.Count == 1)
        {
            var (decision, top, ranked) = Ranking.Decide(byKind.Values.First(), 0.8, 0.25);
            if (decision == Decision.Resolved) return top!.Ref;
            var one = Consume(step.N, field, ClarificationKind.ChooseKind, SaidAs(r), ranked, searchable: false);
            return RememberUnknownPick(r, key, one);
        }

        var rows = byKind.SelectMany(kv => kv.Value.Select(c => c with { Group = kv.Key })).OrderByDescending(c => c.Score).ToList();
        var answer = Consume(step.N, field, ClarificationKind.ChooseKind, SaidAs(r), rows, searchable: false);
        return RememberUnknownPick(r, key, answer);
    }

    private ItemRef? RememberUnknownPick(AssistantRef r, string key, Consumed answer)
    {
        var chosen = answer.Refs.FirstOrDefault();
        if (chosen is null) return null;
        if (key.Length > 0) Learn(BrainEntryKind.Pick, BrainSubject.Words(key, chosen.Kind), chosen.Key, BrainSource.Pick, r.Text);
        return chosen;
    }

    private List<Candidate> AccountCandidates(AssistantRef r)
    {
        var norm = TextMatch.Fold(r.Norm);
        var text = TextMatch.Fold(r.Text);
        var key = TextMatch.Key(r.Norm ?? r.Text);
        var list = new List<Candidate>();
        foreach (var a in _index.Accounts)
        {
            var name = TextMatch.Fold(a.Name);
            if (_brain.Find(BrainEntryKind.AccountNickname).Any(e => e.Subject.AccountId == a.Id && NicknameMatches(e, r)))
                list.Add(new Candidate(ItemRef.Account(a.Id), a.Name, a.Address, null, 1, true));
            else if (name.Length > 0 && (name == norm || name == text))
                list.Add(new Candidate(ItemRef.Account(a.Id), a.Name, a.Address, null, 0.9, false));
            else if (a.AllAddresses.Any(x => TextMatch.Same(x, r.Norm) || TextMatch.Same(x, r.Text)))
                list.Add(new Candidate(ItemRef.Account(a.Id), a.Name, a.Address, null, 1, true));
            else if (TextMatch.Words(r.Norm ?? r.Text).Any(w => w == TextMatch.Fold(a.Account.ProviderType.ToString()) || TextMatch.Words(TextMatch.Domain(a.Address)).Contains(w)))
                list.Add(new Candidate(ItemRef.Account(a.Id), a.Name, a.Address, null, 0.8, false));
        }

        return list;
    }
}
