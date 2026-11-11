using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Session;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

public sealed partial class TurnResolver
{
    /// <summary>
    /// Memory statements (plan 4.8 / 4.9). An entry is <b>stated</b> only when the reading says so, the kind is
    /// teach / command / correct, the request is not about a selection, the subject's words are in the request and
    /// resolved exactly (or were picked in a question this statement raised), and the value is a concept or a
    /// closed-vocabulary preference. Anything else becomes a proposal. Negations forget; a <c>correct</c> replaces.
    /// </summary>
    private async Task ResolveMemoryAsync(CancellationToken ct)
    {
        var statements = (R.Memory ?? []).ToList();
        if (statements.Count == 0) return;
        var kindOk = R.Kind is AssistantReadingKinds.Teach or AssistantReadingKinds.Command or AssistantReadingKinds.Correct;
        // Several named people given the same relation in one statement ("X and Y are my partners") are a set: a group, not
        // two holders of one relation (plan 4.8: sets of people are groups).
        foreach (var set in statements.Where(s => s.Predicate == AssistantMemoryPredicates.Relationship && s.Polarity && Ref(s.Subject) is { } sr && IsNamedPerson(sr))
            .GroupBy(s => TextMatch.Fold(s.Value)).Where(g => g.Key.Length > 0 && g.Select(s => s.Subject).Distinct().Count() >= 2).ToList())
        {
            var members = set.Select(s => s.Subject).Distinct().ToList();
            var first = set.First();
            statements.RemoveAll(set.Contains);
            var setRef = new AssistantRef("set_" + set.Key.Replace(' ', '_'), AssistantRefKinds.Group, AssistantRefBy.Set, first.Text, set.Key, Members: members);
            statements.Add(first with { Subject = setRef.Id, Predicate = AssistantMemoryPredicates.Group, Object = null, Value = set.Key });
            _synthetic[setRef.Id] = setRef;
        }
        var i = 0;
        foreach (var m in statements)
        {
            var field = $"memory[{i++}]";
            var subject = Ref(m.Subject);
            if (subject is null) continue;
            var stated = m.Stated && kindOk && _t.Request.Selection is null && R.Refusal is null && !R.Steps.Any(s => s.Cond is not null)
                && (TextMatch.Quoted(_t.Request.Text, subject.Text) || TextMatch.Quoted(_t.Request.Text, subject.Norm) || subject.Kind == AssistantRefKinds.Self);
            var value = m.Value?.Trim();
            var concept = TextMatch.Fold(value);
            var saidAs = m.Text ?? subject.Text;
            switch (m.Predicate)
            {
                case AssistantMemoryPredicates.Relationship:
                case AssistantMemoryPredicates.Nickname when subject.Kind is AssistantRefKinds.Person or AssistantRefKinds.Unknown:
                case AssistantMemoryPredicates.Label when subject.Kind is AssistantRefKinds.Person or AssistantRefKinds.Unknown:
                {
                    if (m.Predicate == AssistantMemoryPredicates.Label && value is null && !m.Polarity)
                    {
                        await ForgetEverythingAboutAsync(field, subject, ct).ConfigureAwait(false);
                        break;
                    }

                    // The named person may be the object ("my father is Christian": subject the relationship, object the name);
                    // a relationship ref alone ("my dad" in a request) states nothing, but "forget who my uncle is" forgets the concept.
                    var objectPerson = m.Object is { } oid ? Ref(oid) : null;
                    if (!m.Polarity && subject.By == AssistantRefBy.Relationship && objectPerson is null && m.Predicate == AssistantMemoryPredicates.Relationship)
                    {
                        var forgetConcept = TextMatch.IsConcept(concept) ? concept : TextMatch.Fold(subject.Norm);
                        _t.Forget.AddRange(_brain.Find(BrainEntryKind.PersonRelation).Where(e => TextMatch.Same(e.Value, forgetConcept)).Select(e => e.Id));
                        _t.MemoryMessages.Add(AgentMessageKind.Forgotten);
                        break;
                    }

                    // A relationship is remembered only between the user and one person. Two named people linked by a relation
                    // ("X's boss is Y", "in the mail from X it says Y is his boss") is someone else's relation: never stored.
                    if (IsNamedPerson(subject) && objectPerson is not null && IsNamedPerson(objectPerson)) break;
                    // "my cousin is p3" read with the relation word as the name: the subject names no one.
                    if (IsNamedPerson(subject) && objectPerson is null && TextMatch.IsConcept(concept) && (TextMatch.Same(subject.Norm, concept) || TextMatch.Same(subject.Text, "my " + concept) || TextMatch.Words(subject.Norm ?? subject.Text).SequenceEqual(TextMatch.Words(concept)))) break;
                    if (objectPerson is not null && objectPerson.By != AssistantRefBy.Relationship && objectPerson.Kind != AssistantRefKinds.Self && !IsNamedPerson(objectPerson)) break;
                    // "Christian Müller benim muhasebecim" read with the name as a relationship ref: a ref that is not the concept,
                    // next to the relationship ref that is, names the person.
                    if (!IsNamedPerson(subject) && subject.By == AssistantRefBy.Relationship && objectPerson is null && TextMatch.IsConcept(concept)
                        && !ConceptMatches(concept, subject) && TextMatch.Words(subject.Norm ?? subject.Text).Length >= 2 && TextMatch.Quoted(_t.Request.Text, subject.Norm ?? subject.Text)
                        && R.Refs.Any(x => x.Id != subject.Id && x.By == AssistantRefBy.Relationship && ConceptMatches(concept, x)))
                        subject = subject with { By = AssistantRefBy.Name };
                    if (!IsNamedPerson(subject))
                    {
                        if (objectPerson is null || !IsNamedPerson(objectPerson)) break;
                        if (!TextMatch.IsConcept(concept) && subject.By == AssistantRefBy.Relationship) concept = TextMatch.Fold(subject.Norm);
                        // The relation must be the user's own: the subject is the relationship ref (or the user), never a third person.
                        if (subject.By != AssistantRefBy.Relationship && subject.Kind != AssistantRefKinds.Self) break;
                        subject = objectPerson;
                        stated = m.Stated && kindOk && _t.Request.Selection is null && (TextMatch.Quoted(_t.Request.Text, subject.Text) || TextMatch.Quoted(_t.Request.Text, subject.Norm));
                    }

                    if (!TextMatch.IsConcept(concept)) break;
                    var kind = m.Predicate == AssistantMemoryPredicates.Nickname ? BrainEntryKind.PersonAlias : BrainEntryKind.PersonRelation;
                    var storedValue = kind == BrainEntryKind.PersonAlias ? TextMatch.Key(value) : concept;
                    if (!m.Polarity)
                    {
                        var subjectMatches = await ExistingContactsAsync(subject, ct).ConfigureAwait(false);
                        var removed = _brain.Find(kind, storedValue).Where(e => subjectMatches.Count == 0 || subjectMatches.Any(a => string.Equals(a, e.Subject.Address, StringComparison.OrdinalIgnoreCase))).ToList();
                        _t.Forget.AddRange(removed.Select(e => e.Id));
                        _t.MemoryMessages.Add(AgentMessageKind.Forgotten);
                        break;
                    }

                    var (person, exact) = await ResolveStatementPersonAsync(field, subject, ct, ask: stated).ConfigureAwait(false);
                    if (person is null) break;
                    var entrySubject = BrainSubject.Contact(person.Address, person.Name);
                    if (stated && exact)
                    {
                        // A stated relation replaces the previous holder of the concept (a correction or a plain restatement); sets of people are groups.
                        Learn(kind, entrySubject, storedValue, BrainSource.Stated, saidAs, replace: kind == BrainEntryKind.PersonRelation);
                        _t.MemoryMessages.Add(AgentMessageKind.Remembered);
                    }
                    else _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), kind, entrySubject, storedValue, saidAs ?? ""));
                    break;
                }

                case AssistantMemoryPredicates.Nickname when subject.Kind == AssistantRefKinds.Account:
                case AssistantMemoryPredicates.Label when subject.Kind == AssistantRefKinds.Account:
                case AssistantMemoryPredicates.Synonym when subject.Kind == AssistantRefKinds.Account:
                    await ResolveAccountNicknameAsync(m, subject, field, stated, saidAs, ct).ConfigureAwait(false);
                    break;

                case AssistantMemoryPredicates.Synonym when subject.Kind == AssistantRefKinds.Container:
                case AssistantMemoryPredicates.Label when subject.Kind == AssistantRefKinds.Container:
                case AssistantMemoryPredicates.Nickname when subject.Kind == AssistantRefKinds.Container:
                {
                    if (!TextMatch.IsConcept(concept)) break;
                    var scope = AccountScope.All;
                    if (m.Object is { } objId && Ref(objId) is { Kind: AssistantRefKinds.Account } accRef)
                    {
                        var acc = await ResolveAccountAsync(new AssistantStep(0, AssistantActions.SyncMail, null, null, null, false, null, [], 1, null, null), accRef, field + ".account", ct).ConfigureAwait(false);
                        if (acc is not null) scope = AccountScope.Of(acc.Id);
                    }

                    concept = ConceptOf(m, subject);
                    if (!TextMatch.IsConcept(concept)) break;
                    var kind = KindOfType(subject.Props?.Type) ?? ItemKind.Folder;
                    var candidates = ContainerCandidates(subject, kind, scope.Ids).ToList();
                    if (!candidates.Any(c => c.Score >= 0.95))
                    {
                        // The container named elsewhere in the statement ("the App Demo folder ... is my bills folder", "BurakFolder benim ..."):
                        // another container ref by name, or a container whose name is quoted in the request.
                        foreach (var other in R.Refs.Where(x => x.Id != subject.Id && x.Kind == AssistantRefKinds.Container && x.By == AssistantRefBy.Name))
                            candidates.AddRange(ContainerCandidates(other, kind, scope.Ids).Where(c => c.Score >= 0.95));
                        foreach (var c in _index.ContainersOf(kind, scope.Ids))
                        {
                            var nameKey = TextMatch.Key(c.Name);
                            if (nameKey.Length >= 5 && TextMatch.Quoted(_t.Request.Text, c.Name)) candidates.Add(ContainerToCandidate(c, 0.95, false));
                        }
                    }

                    var (decision, top, ranked) = Ranking.Decide(candidates, 0.9, 0.2);
                    DomainIndex.Container? container;
                    var exact = decision == Decision.Resolved;
                    if (decision == Decision.Resolved) container = Find(top!.Ref);
                    else
                    {
                        if (!stated) break;
                        var options = ranked.Count > 0 ? ranked : _index.ContainersOf(kind, scope.Ids).Select(c => ContainerToCandidate(c, 0, false)).ToList();
                        if (options.Count == 0) break;
                        var answer = Consume(0, field, ClarificationKind.PickContainer, subject.Text ?? subject.Norm ?? "", options, searchable: true, filter: subject.Norm ?? subject.Text);
                        container = answer.Refs.Select(Find).OfType<DomainIndex.Container>().FirstOrDefault();
                        exact = container is not null;
                    }

                    if (container is null) break;
                    var entrySubject = BrainSubject.Container(container.Kind, container.AccountId, container.Id, container.Name);
                    if (!m.Polarity) { _t.Forget.AddRange(_brain.Find(BrainEntryKind.ContainerSynonym, concept).Where(e => e.Subject.ContainerId == container.Id).Select(e => e.Id)); _t.MemoryMessages.Add(AgentMessageKind.Forgotten); break; }
                    if (stated && exact) { Learn(BrainEntryKind.ContainerSynonym, entrySubject, concept, BrainSource.Stated, saidAs); _t.MemoryMessages.Add(AgentMessageKind.Remembered); }
                    else _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.ContainerSynonym, entrySubject, concept, saidAs ?? ""));
                    break;
                }

                case AssistantMemoryPredicates.Label when subject.Kind == AssistantRefKinds.Org:
                case AssistantMemoryPredicates.Synonym when subject.Kind == AssistantRefKinds.Org:
                case AssistantMemoryPredicates.Nickname when subject.Kind == AssistantRefKinds.Org:
                {
                    if (!TextMatch.IsConcept(concept)) break;
                    var domainOrAddress = TextMatch.LooksLikeAddress(subject.Norm) ? subject.Norm!.Trim() : TextMatch.LooksLikeDomain(subject.Norm) ? TextMatch.Fold(subject.Norm)
                        : subject.Props?.Domain is { } pd && TextMatch.LooksLikeDomain(pd) ? TextMatch.Fold(pd) : TextMatch.LooksLikeDomain(subject.Text) ? TextMatch.Fold(subject.Text) : null;
                    var exact = domainOrAddress is not null;
                    if (domainOrAddress is null)
                    {
                        var org = await ResolveOrgFilterAsync(new AssistantStep(0, AssistantActions.SearchMail, null, null, null, false, null, [], 1, null, null), subject with { By = AssistantRefBy.Description }, field, ct).ConfigureAwait(false);
                        domainOrAddress = org.Addresses.FirstOrDefault() ?? org.Domains.FirstOrDefault();
                        exact = domainOrAddress is not null;
                    }

                    if (domainOrAddress is null) break;
                    var entrySubject = BrainSubject.Org(domainOrAddress, TextMatch.LooksLikeDomain(subject.Text) || TextMatch.LooksLikeAddress(subject.Text) ? null : subject.Text);
                    if (!m.Polarity) { _t.Forget.AddRange(_brain.Find(BrainEntryKind.SenderLabel, concept).Where(e => string.Equals(e.Subject.Address, domainOrAddress, StringComparison.OrdinalIgnoreCase)).Select(e => e.Id)); _t.MemoryMessages.Add(AgentMessageKind.Forgotten); break; }
                    if (stated && exact) { Learn(BrainEntryKind.SenderLabel, entrySubject, concept, BrainSource.Stated, saidAs); _t.MemoryMessages.Add(AgentMessageKind.Remembered); }
                    else _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.SenderLabel, entrySubject, concept, saidAs ?? ""));
                    break;
                }

                case AssistantMemoryPredicates.Synonym when subject.Kind == AssistantRefKinds.Item:
                case AssistantMemoryPredicates.Label when subject.Kind == AssistantRefKinds.Item:
                {
                    // "promos are the newsletters": the user's words for a sender kind.
                    var senderKind = SenderKindOf(subject.Props?.ItemType);
                    if (senderKind == SenderKind.Any) senderKind = SenderKindOf(value);
                    var words = senderKind != SenderKind.Any && SenderKindOf(value) == senderKind ? subject.Text ?? subject.Norm : value;
                    if (senderKind == SenderKind.Any || !TextMatch.IsConcept(words)) break;
                    var entrySubject = BrainSubject.Words(TextMatch.Fold(words), null);
                    if (stated) { Learn(BrainEntryKind.FilterWord, entrySubject, "senderKind=" + senderKind.ToString().ToLowerInvariant(), BrainSource.Stated, saidAs); _t.MemoryMessages.Add(AgentMessageKind.Remembered); }
                    else _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.FilterWord, entrySubject, "senderKind=" + senderKind.ToString().ToLowerInvariant(), saidAs ?? ""));
                    break;
                }

                case AssistantMemoryPredicates.Group:
                    await ResolveGroupStatementAsync(m, subject, field, stated, saidAs, ct).ConfigureAwait(false);
                    break;

                case AssistantMemoryPredicates.Default:
                    await ResolveDefaultStatementAsync(m, subject, field, stated, saidAs, ct).ConfigureAwait(false);
                    break;

                case AssistantMemoryPredicates.Preference:
                    await ResolvePreferenceStatementAsync(m, subject, field, stated, saidAs, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>"my Gmail is my school account": the concept becomes the account's nickname.</summary>
    private async Task ResolveAccountNicknameAsync(AssistantMemoryStatement m, AssistantRef subject, string field, bool stated, string? saidAs, CancellationToken ct)
    {
        var concept = ConceptOf(m, subject);
        if (!TextMatch.IsConcept(concept)) return;
        var account = await StatementAccountAsync(m, subject, field, stated, ct).ConfigureAwait(false);
        if (account is null) return;
        if (!m.Polarity) { _t.Forget.AddRange(_brain.Find(BrainEntryKind.AccountNickname, concept).Where(e => e.Subject.AccountId == account.Id).Select(e => e.Id)); _t.MemoryMessages.Add(AgentMessageKind.Forgotten); return; }
        if (stated) { Learn(BrainEntryKind.AccountNickname, BrainSubject.Account(account.Id, account.Name), concept, BrainSource.Stated, saidAs); _t.MemoryMessages.Add(AgentMessageKind.Remembered); }
        else _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.AccountNickname, BrainSubject.Account(account.Id, account.Name), concept, saidAs ?? ""));
    }

    /// <summary>The concept of a statement: the subject's canonical norm when the value is the user's own words for it ("alışveriş" in "alışveriş hesabım"), else the value.</summary>
    private string ConceptOf(AssistantMemoryStatement m, AssistantRef subject)
    {
        var value = TextMatch.Fold(m.Value);
        foreach (var r in new[] { subject, m.Object is { } o ? Ref(o) : null }.OfType<AssistantRef>())
        {
            var norm = TextMatch.Fold(r.Norm);
            if (value.Length > 0 && norm.Length > 0 && value != norm && TextMatch.Quoted(r.Text, value) && !TextMatch.Quoted(r.Text, norm) && TextMatch.IsConcept(norm) && r.By is AssistantRefBy.Description or AssistantRefBy.Nickname or AssistantRefBy.Purpose) return norm;
        }

        return value.Length > 0 ? value : TextMatch.Fold(subject.Norm);
    }

    /// <summary>The account a statement is about: the subject when it resolves without a question, else another account ref of the reading that does, else an account whose address or name is quoted in the request, else the subject with a question.</summary>
    private async Task<DomainIndex.AccountEntry?> StatementAccountAsync(AssistantMemoryStatement m, AssistantRef subject, string field, bool ask, CancellationToken ct)
    {
        var refs = new List<AssistantRef> { subject };
        if (m.Object is { } o && Ref(o) is { Kind: AssistantRefKinds.Account } obj) refs.Add(obj);
        refs.AddRange(R.Refs.Where(x => x.Kind == AssistantRefKinds.Account && !refs.Contains(x)));
        foreach (var r in refs)
        {
            var exact = AccountCandidates(r).Where(c => c.Score >= 0.9).DistinctBy(c => c.Ref.Key).ToList();
            if (exact.Count == 1) return _index.Account(exact[0].Ref.AccountId);
        }

        var quoted = _index.Accounts.Where(a => (TextMatch.Key(TextMatch.LocalPart(a.Address)).Length >= 4 && TextMatch.Quoted(_t.Request.Text, TextMatch.LocalPart(a.Address)))
            || (TextMatch.Key(a.Name).Length >= 4 && TextMatch.Quoted(_t.Request.Text, a.Name))).ToList();
        if (quoted.Count == 1) return quoted[0];
        // An inferred statement never asks (plan 4.8): it becomes a proposal only when its subject resolves exactly.
        if (!ask) return null;
        return await ResolveAccountAsync(new AssistantStep(0, AssistantActions.SyncMail, null, null, null, false, null, [], 1, null, null), subject, field, ct).ConfigureAwait(false);
    }

    /// <summary>A person named by a name (letters), a description or an address; "p3" is not a name.</summary>
    private static bool IsNamedPerson(AssistantRef r) => r.Kind is AssistantRefKinds.Person or AssistantRefKinds.Unknown && r.By is AssistantRefBy.Name or AssistantRefBy.Description or AssistantRefBy.Address or null
        && (TextMatch.IsConcept(r.Norm ?? r.Text) || TextMatch.LooksLikeAddress(r.Norm ?? r.Text));

    private async Task<(Recipient? Person, bool Exact)> ResolveStatementPersonAsync(string field, AssistantRef subject, CancellationToken ct, bool ask = true)
    {
        if (subject.Kind == AssistantRefKinds.Self) return (null, false);
        var candidates = await NameCandidatesAsync(subject, ct).ConfigureAwait(false);
        var exactOnes = candidates.Where(c => c.Score >= 0.999).GroupBy(c => c.Ref.Key).ToList();
        if (exactOnes.Count == 1) return (ToRecipient(exactOnes[0].First()), true);
        var (decision, top, ranked) = Ranking.Decide(candidates, 0.8, 0.25);
        if (decision == Decision.Resolved && top!.Exact) return (ToRecipient(top), true);
        if (!ask) return decision == Decision.Resolved ? (ToRecipient(top!), false) : (null, false);
        var answer = Consume(0, field, ClarificationKind.PickPerson, subject.Text ?? subject.Norm ?? "", ranked, searchable: true, newAddress: true, suggested: ranked.FirstOrDefault(), filter: subject.Norm ?? subject.Text);
        var chosen = answer.Chosen.FirstOrDefault(c => c.Ref.Kind == ItemKind.Contact);
        if (chosen is not null) return (ToRecipient(chosen), true);
        if (answer.NewAddress is { } na && TextMatch.LooksLikeAddress(na)) return (new Recipient(na.Trim(), subject.Norm ?? subject.Text), true);
        return (null, false);
    }

    private async Task<List<string>> ExistingContactsAsync(AssistantRef subject, CancellationToken ct)
    {
        var candidates = await NameCandidatesAsync(subject, ct).ConfigureAwait(false);
        return candidates.Where(c => c.Score >= 0.8).Select(c => c.Ref.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"forget everything about Jay": confirm with the entries listed, then tombstone the chosen ones.</summary>
    private async Task ForgetEverythingAboutAsync(string field, AssistantRef subject, CancellationToken ct)
    {
        var addresses = await ExistingContactsAsync(subject, ct).ConfigureAwait(false);
        var key = TextMatch.Key(subject.Norm ?? subject.Text);
        var entries = _brain.Entries.Where(e =>
            (e.Subject.Type == BrainSubjectType.Contact && addresses.Any(a => string.Equals(a, e.Subject.Address, StringComparison.OrdinalIgnoreCase)))
            || (e.Subject.Type == BrainSubjectType.Contact && TextMatch.Key(e.Subject.Name) == key)
            || (e.Kind == BrainEntryKind.Pick && e.Subject.Text == key)
            || (e.Kind == BrainEntryKind.Group && addresses.Any(a => e.Members.Any(m => m.EndsWith(":" + a, StringComparison.OrdinalIgnoreCase))))).ToList();
        if (entries.Count == 0) { _t.MemoryMessages.Add(AgentMessageKind.Forgotten); return; }
        var options = entries.Select(e => new Candidate(new ItemRef(ItemKind.BrainEntry, null, e.Id), $"{e.Kind}: {e.Value}", e.Subject.Name ?? e.Subject.Address, null, 1, true)).ToList();
        var answer = Consume(0, field, ClarificationKind.ConfirmForget, subject.Text ?? subject.Norm ?? "", options, many: true, searchable: false);
        var ids = answer.Refs.Where(r => r.Kind == ItemKind.BrainEntry).Select(r => r.Id).ToList();
        foreach (var id in ids)
        {
            var entry = entries.First(e => e.Id == id);
            if (entry.Kind == BrainEntryKind.Group)
            {
                var keep = entry.Members.Where(m => !addresses.Any(a => m.EndsWith(":" + a, StringComparison.OrdinalIgnoreCase))).ToList();
                _t.Forget.Add(id);
                if (keep.Count > 0) Learn(BrainEntryKind.Group, entry.Subject, string.Join(';', keep), entry.Source, null);
            }
            else _t.Forget.Add(id);
        }

        _t.MemoryMessages.Add(AgentMessageKind.Forgotten);
    }

    /// <summary>"Jay and Christian are my team" (subject: the set, value: the concept); "add Shiva to the team" (subject: a person, value: the concept); "remove Shiva from the team" (polarity false).</summary>
    private async Task ResolveGroupStatementAsync(AssistantMemoryStatement m, AssistantRef subject, string field, bool stated, string? saidAs, CancellationToken ct)
    {
        var concept = TextMatch.Fold(m.Value);
        var objectRef = m.Object is { } o ? Ref(o) : null;
        // The concept may sit on the object ref ("Jay is in the team" -> object: group ref "the team") when the value is empty.
        if (!TextMatch.IsConcept(concept) && objectRef is { Kind: AssistantRefKinds.Group }) concept = TextMatch.Fold(objectRef.Norm ?? objectRef.Text);
        if (!TextMatch.IsConcept(concept))
        {
            // The subject may be the group and the object the member.
            if (subject.Kind == AssistantRefKinds.Group && objectRef is not null && TextMatch.IsConcept(TextMatch.Fold(subject.Norm)))
            {
                concept = TextMatch.Fold(subject.Norm);
                (subject, objectRef) = (objectRef, subject);
            }
            else return;
        }

        var members = new List<Recipient>();
        var allExact = true;
        var i = 0;
        var memberRefs = Expand(subject).Where(r => r.Kind is AssistantRefKinds.Person or AssistantRefKinds.Unknown).ToList();
        // "Jay and Sandy are my partners" read with Sandy as the object: the object is a member too.
        if (objectRef is not null && objectRef.Kind is AssistantRefKinds.Person && IsNamedPerson(objectRef) && !memberRefs.Contains(objectRef)) memberRefs.Add(objectRef);
        foreach (var memberRef in memberRefs)
        {
            var (person, exact) = await ResolveStatementPersonAsync($"{field}.{i++}", memberRef, ct, ask: stated).ConfigureAwait(false);
            if (person is null) continue;
            members.Add(person);
            allExact &= exact;
        }

        if (members.Count == 0) return;
        var keys = members.Select(p => ItemRef.Contact(p.Address).Key).ToList();
        foreach (var p in members) Learn(BrainEntryKind.Pick, BrainSubject.Words(TextMatch.Key(p.Name ?? ""), ItemKind.Contact), ItemRef.Contact(p.Address).Key, BrainSource.Pick, null);
        var existing = _brain.Find(BrainEntryKind.Group).FirstOrDefault(g => TextMatch.Same(g.Subject.Text, concept));
        // Two statements of one turn ("Jay and Sandy are my partners" read as two) add up.
        var pendingIndex = _t.Learned.FindIndex(l => l.Kind == BrainEntryKind.Group && TextMatch.Same(l.Subject.Text, concept));
        if (pendingIndex >= 0 && m.Polarity)
        {
            var pending = _t.Learned[pendingIndex];
            keys = pending.Value.Split(';', StringSplitOptions.RemoveEmptyEntries).Concat(keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _t.Learned.RemoveAt(pendingIndex);
        }

        if (!m.Polarity)
        {
            if (existing is null) return;
            var keep = existing.Members.Where(k => !keys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            _t.Forget.Add(existing.Id);
            if (keep.Count > 0) Learn(BrainEntryKind.Group, BrainSubject.Concept(concept), string.Join(';', keep), existing.Source, saidAs);
            _t.MemoryMessages.Add(AgentMessageKind.Forgotten);
            return;
        }

        // A set defines the group; a single person joins it.
        var defines = subject.Kind == AssistantRefKinds.Group || subject.By == AssistantRefBy.Set || members.Count > 1 || pendingIndex >= 0;
        var all = defines ? keys : (existing?.Members ?? []).Concat(keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!defines && existing is not null) _t.Forget.Add(existing.Id);
        if (stated && allExact)
        {
            Learn(BrainEntryKind.Group, BrainSubject.Concept(concept), string.Join(';', all), BrainSource.Stated, saidAs, replace: true);
            _t.MemoryMessages.Add(AgentMessageKind.Remembered);
        }
        else _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.Group, BrainSubject.Concept(concept), string.Join(';', all), saidAs ?? ""));
    }

    /// <summary>"use my Gmail for new mails": value names the use (mail, task, calendar, taskList) and the subject or object names the account or container.</summary>
    private async Task ResolveDefaultStatementAsync(AssistantMemoryStatement m, AssistantRef subject, string field, bool stated, string? saidAs, CancellationToken ct)
    {
        var raw = TextMatch.Fold(m.Value);
        var use = raw.Split('=')[0].Trim() switch
        {
            "mail" or "mails" or "email" or "draft" or "drafts" => "mail",
            "task" or "tasks" => "task",
            "calendar" or "event" or "events" => "calendar",
            "tasklist" or "list" => "taskList",
            _ => null,
        };
        var objectRef = m.Object is { } o ? Ref(o) : null;
        var thing = subject.Kind is AssistantRefKinds.Account or AssistantRefKinds.Container ? subject : objectRef?.Kind is AssistantRefKinds.Account or AssistantRefKinds.Container ? objectRef : null;
        if (thing is null) return;
        use ??= thing.Kind == AssistantRefKinds.Container ? (KindOfType(thing.Props?.Type) == ItemKind.Calendar ? "calendar" : KindOfType(thing.Props?.Type) == ItemKind.TaskList ? "taskList" : null) : null;
        if (use is null)
        {
            // "my account X is my startup account" read as a default: a concept value on an account is its nickname.
            if (thing.Kind == AssistantRefKinds.Account && TextMatch.IsConcept(raw)) await ResolveAccountNicknameAsync(m, thing, field, stated, saidAs, ct).ConfigureAwait(false);
            return;
        }
        ItemRef? target = null;
        var step = new AssistantStep(0, AssistantActions.SyncMail, null, null, null, false, null, [], 1, null, null);
        if (thing.Kind == AssistantRefKinds.Account)
        {
            var account = await StatementAccountAsync(m, thing, field, stated, ct).ConfigureAwait(false);
            if (account is null) return;
            if (use is "calendar" && _index.Primary(ItemKind.Calendar, account.Id) is { } pc) target = pc.Ref;
            else if (use is "taskList" && _index.Primary(ItemKind.TaskList, account.Id) is { } pl) target = pl.Ref;
            else target = ItemRef.Account(account.Id);
        }
        else
        {
            var kind = use == "calendar" ? ItemKind.Calendar : use == "taskList" ? ItemKind.TaskList : KindOfType(thing.Props?.Type) ?? ItemKind.Calendar;
            if (kind == ItemKind.Calendar) use = "calendar";
            if (kind == ItemKind.TaskList) use = "taskList";
            var candidates = ContainerCandidates(thing, kind, null).ToList();
            var (decision, top, ranked) = Ranking.Decide(candidates, 0.9, 0.2);
            if (decision == Decision.Resolved) target = top!.Ref;
            else
            {
                if (!stated) return;
                var options = ranked.Count > 0 ? ranked : _index.ContainersOf(kind, null).Select(c => ContainerToCandidate(c, 0, false)).ToList();
                if (options.Count == 0) return;
                var answer = Consume(0, field, ClarificationKind.PickContainer, thing.Text ?? thing.Norm ?? "", options, searchable: true, filter: thing.Norm ?? thing.Text);
                target = answer.Refs.FirstOrDefault();
            }
        }

        if (target is null) return;
        var value = $"{use}={target.Key}";
        if (!m.Polarity) { _t.Forget.AddRange(_brain.Find(BrainEntryKind.Default, value).Select(e => e.Id)); _t.MemoryMessages.Add(AgentMessageKind.Forgotten); return; }
        if (stated)
        {
            // One default per use: the new one replaces the old.
            _t.Forget.AddRange(_brain.Find(BrainEntryKind.Default).Where(e => e.Value.StartsWith(use + "=", StringComparison.Ordinal) && e.Value != value).Select(e => e.Id));
            Learn(BrainEntryKind.Default, BrainSubject.Self(), value, BrainSource.Stated, saidAs);
            _t.MemoryMessages.Add(AgentMessageKind.Remembered);
        }
        else _t.Proposals.Add(new BrainProposal(Guid.NewGuid(), BrainEntryKind.Default, BrainSubject.Self(), value, saidAs ?? ""));
    }

    /// <summary>A preference is stated only (plan 4.8): closed vocabulary, subject a recipient, an account or the user.</summary>
    private async Task ResolvePreferenceStatementAsync(AssistantMemoryStatement m, AssistantRef subject, string field, bool stated, string? saidAs, CancellationToken ct)
    {
        var raw = (m.Value ?? "").Trim();
        var eq = raw.IndexOf('=');
        if (eq <= 0) return;
        var key = raw[..eq].Trim().ToLowerInvariant();
        var value = raw[(eq + 1)..].Trim();
        var ok = key switch
        {
            "language" => value.Length is >= 2 and <= 12 && value.All(c => char.IsLetter(c) || c == '-'),
            "tone" => AssistantHints.Tones.Contains(value, StringComparer.OrdinalIgnoreCase),
            "length" => AssistantHints.Lengths.Contains(value, StringComparer.OrdinalIgnoreCase),
            "signoff" => AssistantHints.Signoffs.Contains(value, StringComparer.OrdinalIgnoreCase),
            _ => false,
        };
        if (!ok) return;
        var normalized = key == "language" ? $"language={value.ToLowerInvariant()}" : $"{key}={(key == "signoff" ? value : value.ToLowerInvariant())}";
        BrainSubject? entrySubject = null;
        if (subject.Kind == AssistantRefKinds.Self || subject.By == AssistantRefBy.Self) entrySubject = BrainSubject.Self();
        else if (subject.Kind == AssistantRefKinds.Account)
        {
            var account = await ResolveAccountAsync(new AssistantStep(0, AssistantActions.SyncMail, null, null, null, false, null, [], 1, null, null), subject, field, ct).ConfigureAwait(false);
            if (account is not null) entrySubject = BrainSubject.Account(account.Id, account.Name);
        }
        else
        {
            var (person, exact) = await ResolveStatementPersonAsync(field, subject, ct).ConfigureAwait(false);
            if (person is null || !exact) return;
            entrySubject = BrainSubject.Contact(person.Address, person.Name);
        }

        if (entrySubject is null) return;
        if (!m.Polarity) { _t.Forget.AddRange(_brain.Find(BrainEntryKind.Preference, normalized).Where(e => e.Subject.Key == entrySubject.Key).Select(e => e.Id)); _t.MemoryMessages.Add(AgentMessageKind.Forgotten); return; }
        if (!stated) return;
        _t.Forget.AddRange(_brain.PreferencesFor(entrySubject.Key).Where(e => e.Value.StartsWith(key + "=", StringComparison.Ordinal) && e.Value != normalized).Select(e => e.Id));
        Learn(BrainEntryKind.Preference, entrySubject, normalized, BrainSource.Stated, saidAs);
        _t.MemoryMessages.Add(AgentMessageKind.Remembered);
    }

    /// <summary>"add Shiva to the team" / "Christian is not in the team anymore" read as a contact-list action: when the list names a brain group (and no contact list), it is a group edit and the turn ends with Remembered.</summary>
    private async Task TryGroupEditAsync(AssistantStep step, bool add, CancellationToken ct)
    {
        var listRef = Refs(step, "list").FirstOrDefault();
        if (listRef is null) return;
        var group = _brain.Find(BrainEntryKind.Group).FirstOrDefault(g => ConceptMatches(g.Subject.Text, listRef) );
        if (group is null || _index.ContainersOf(ItemKind.ContactList, null).Any(c => TextMatch.Same(c.Name, listRef.Norm) || TextMatch.Same(c.Name, listRef.Text))) return;
        var members = new List<string>();
        var i = 0;
        foreach (var r in Refs(step, "person").SelectMany(Expand))
        {
            var (person, _) = await ResolveStatementPersonAsync($"group[{i++}]", r, ct).ConfigureAwait(false);
            if (person is not null) members.Add(ItemRef.Contact(person.Address).Key);
        }

        if (members.Count == 0) return;
        var all = add ? group.Members.Concat(members).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : group.Members.Where(k => !members.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        _t.Forget.Add(group.Id);
        if (all.Count > 0) Learn(BrainEntryKind.Group, group.Subject, string.Join(';', all), BrainSource.Stated, step.Gloss ?? listRef.Text);
        throw new TurnMessageException(add ? AgentMessageKind.Remembered : AgentMessageKind.Forgotten, step.Gloss);
    }

    /// <summary>A stored concept matches a ref when it equals the norm or text, or every word of it is among the norm's words ("bills" in "bills folder").</summary>
    private static bool ConceptMatches(string? concept, AssistantRef r)
    {
        if (string.IsNullOrEmpty(concept)) return false;
        if (TextMatch.Same(concept, r.Norm) || TextMatch.Same(concept, r.Text)) return true;
        var cw = TextMatch.Words(concept);
        if (cw.Length == 0) return false;
        foreach (var nw in new[] { TextMatch.Words(r.Norm), TextMatch.Words(r.Text) })
        {
            if (nw.Length == 0) continue;
            var (shorter, longer) = cw.Length <= nw.Length ? (cw, nw) : (nw, cw);
            if (shorter.Length > 0 && shorter.All(longer.Contains)) return true;
        }

        // One-word concepts that share their first five letters ("neighbour" / "neighbor") are the same concept.
        var c1 = TextMatch.Key(concept);
        var n1 = TextMatch.Key(r.Norm);
        if (cw.Length == 1 && c1.Length >= 5 && n1.Length >= 5 && !n1.Contains(' ') && c1[..5] == n1[..5]) return true;
        return (r.Alt ?? []).Any(a => TextMatch.Same(concept, a.Norm));
    }
}
