using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Reading;
using Wino.Agent.Runtime.Session;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

public sealed partial class TurnResolver
{
    /// <summary>The item ref a step acts on; for the first step also an item ref the classifier left unattached ("mark them all done" with "them" beside the step).</summary>
    private AssistantRef? Item(AssistantStep step) => step.On is { } on && on != AssistantStep.OnSelection ? Ref(on)
        : step.On is null && step.N == 1 && step.From is null ? OrphanRefs(AssistantRefKinds.Item).FirstOrDefault() : null;

    /// <summary>
    /// The part of a list of items a scope names, in the order the app gave them: the N-th, the first N, the latest or
    /// the oldest; all of them otherwise. Null when the list has no such part (the third of two): never all of them.
    /// </summary>
    private static IReadOnlyList<ItemRef>? ScopeOf(string? scope, IReadOnlyList<ItemRef> items)
    {
        if (scope is null || items.Count == 0) return items;
        if (scope.StartsWith(AssistantScopes.OrdinalPrefix, StringComparison.Ordinal) && int.TryParse(scope[AssistantScopes.OrdinalPrefix.Length..], out var ordinal))
            return ordinal >= 1 && ordinal <= items.Count ? [items[ordinal - 1]] : null;
        if (scope.StartsWith(AssistantScopes.CountPrefix, StringComparison.Ordinal) && int.TryParse(scope[AssistantScopes.CountPrefix.Length..], out var count) && count >= 1) return items.Take(count).ToList();
        return scope switch
        {
            AssistantScopes.Latest => [items[0]],
            AssistantScopes.Oldest => [items[^1]],
            _ => items,
        };
    }

    /// <summary>A scope that names a position in a list rather than a quantity: the N-th, the first N, the latest or the oldest.</summary>
    private static bool IsPositional(string? scope) => scope is AssistantScopes.Latest or AssistantScopes.Oldest
        || (scope is not null && (scope.StartsWith(AssistantScopes.OrdinalPrefix, StringComparison.Ordinal) || scope.StartsWith(AssistantScopes.CountPrefix, StringComparison.Ordinal)));

    /// <summary>The items a step acts on out of the ones at hand; a position the list has not ("the third one" of two) is nothing to act on, never all of them.</summary>
    private static (StepTarget Target, int? Count) PartOf(AssistantStep step, ItemKind kind, IReadOnlyList<ItemRef> items)
    {
        var part = ScopeOf(step.Scope, items) ?? throw new TurnMessageException(AgentMessageKind.NothingMatched, step.Gloss);
        return (ItemsTarget(kind, part), part.Count);
    }

    private static int LimitOf(string? scope) => scope is null ? 0
        : scope.StartsWith(AssistantScopes.CountPrefix, StringComparison.Ordinal) ? int.Parse(scope[AssistantScopes.CountPrefix.Length..])
        : scope is AssistantScopes.Latest or AssistantScopes.Oldest or AssistantScopes.One ? 1
        : scope.StartsWith(AssistantScopes.OrdinalPrefix, StringComparison.Ordinal) ? int.Parse(scope[AssistantScopes.OrdinalPrefix.Length..])
        : 0;

    private static string? ContentText(AssistantRef? r) => r is null ? null
        : !string.IsNullOrWhiteSpace(r.Content?.Text) ? r.Content.Text
        : r.Content?.Points is { Count: > 0 } p ? string.Join("\n", p)
        : r.Text;

    private ResolvedTime TimeOf(AssistantStep step, string argName) => Refs(step, argName).FirstOrDefault() is { } r ? TimeResolver.Resolve(r, _clock) : ResolvedTime.Empty;

    private (DateTime? From, DateTime? To) Period(AssistantStep step, string argName, AssistantRef? item)
    {
        var t = TimeOf(step, argName);
        if (t.Start is null && t.End is null && item?.Time is not null) t = TimeResolver.Resolve(item, _clock);
        if (t.Start is null && t.End is null)
        {
            // An orphan time ref the classifier did not attach to the step.
            var orphan = R.Refs.FirstOrDefault(r => r.Kind == AssistantRefKinds.Time && !R.Steps.Any(s => s.Args.Any(a => a.Ref == r.Id || a.Refs?.Contains(r.Id) == true)));
            if (orphan is not null && step.N == 1) t = TimeResolver.Resolve(orphan, _clock);
        }

        var to = t.End ?? (t.Start is { } s && t.AllDay ? s.Date.AddDays(1) : t.Start is { } s2 && s2.TimeOfDay == TimeSpan.Zero ? s2.AddDays(1) : t.End);
        return (t.Start, to);
    }

    // ---- targets -------------------------------------------------------------------------------------------------

    private async Task<(StepTarget Target, int? Count)> ResolveTargetAsync(AssistantStep step, AgentAction action, AccountScope scope, CancellationToken ct)
    {
        var item = Item(step);
        // A creation about the selection ("write her that..." with a contact selected) acts on nothing existing: the selection
        // answers its people and its account, not its target.
        if (step.On == AssistantStep.OnSelection && ActionCatalog.Creates(action)) return (StepTarget.None.Instance, null);
        if (step.On == AssistantStep.OnSelection || (item is null && _t.Request.Selection is not null && !ActionCatalog.Creates(action) && ActionCatalog.ItemKindOf(action) is { } k0 && SelectionKindMatches(k0)))
        {
            // "archive the first one" with several selected: the part of the selection the scope names, in the app's order.
            var selection = _t.Request.Selection;
            if (selection is { Items.Count: > 0 }) return PartOf(step, ActionCatalog.ItemKindOf(action) ?? ItemKind.Mail, selection.Items);
        }

        // "them", "these" with no selection, an ellipsis or a correction after a turn that ran, or a bare action with nothing
        // named right after a find ("mark them read" with the pronoun dropped): the items the previous turn touched.
        var ownFilters = step.Args.Any(a => FilterArgs.Contains(a.Name) && (a.Ref is not null || a.Refs is { Count: > 0 }));
        var bare = item is null && step.From is null && step.N == 1 && !ownFilters;
        // An item whose words the user did not say was copied from the previous turn by the classifier: it points back too,
        // unless the step filters anew ("and the ones from Christian too").
        var copied = !ownFilters && item is { Text.Length: > 0 } && item.By != AssistantRefBy.Pronoun && !TextMatch.Quoted(_t.Request.Text, item.Text) && !TextMatch.Quoted(_t.Request.Text, item.Norm);
        var pointsBack = item is { By: AssistantRefBy.Pronoun } || _previousItemsSteps.Contains(step.N) || (step.On == AssistantStep.OnSelection && _t.Request.Selection is null && !ownFilters) || bare || copied;
        if (pointsBack && !ActionCatalog.Creates(action) && ActionCatalog.ItemKindOf(action) is { } pk)
        {
            var kind = pk == ItemKind.Draft ? ItemKind.Mail : pk;
            var touched = _ctx.LastTouched(Now, kind);
            if (_t.Request.Selection is { Items.Count: > 0 } sel2)
            {
                // "and flag the second one" after "archive the first one" on a selection: a position counts in the list the
                // user is looking at (the selection) when the previous turn acted on part of it, not in what that turn touched.
                if (touched.Count == 0 || (IsPositional(step.Scope) && touched.All(i => sel2.Items.Any(s => s.Key == i.Key)))) touched = sel2.Items;
            }

            if (touched.Count == 0)
            {
                // A pronoun with nothing to bind to needs the previous request; a terse action word stands on its own as a query.
                if (item is { By: AssistantRefBy.Pronoun }) throw new TurnMessageException(AgentMessageKind.NeedsPreviousRequest, SaidAs(item));
            }
            else return PartOf(step, kind, touched);
        }

        var target = ActionCatalog.Target(action);
        switch (target)
        {
            case AssistantTargets.Mails:
                var query = await BuildMailQueryAsync(step, action, item, scope, ct).ConfigureAwait(false);
                var mailScope = scope.One ?? (query.FolderIds.Count > 0 ? _index.Containers.FirstOrDefault(c => c.Kind == ItemKind.Folder && c.Id == query.FolderIds[0])?.AccountId : null);
                if (query.FolderIds.Count > 0 && query.FolderIds.Select(f => _index.Containers.FirstOrDefault(c => c.Id == f)?.AccountId).Distinct().Count() > 1) mailScope = null;
                int? count = null;
                if (!ActionCatalog.IsFind(action) && action is not (AgentAction.Summarize or AgentAction.Translate or AgentAction.CopyCode))
                {
                    try { count = await _q.CountMailsAsync(mailScope, query, ct).ConfigureAwait(false); }
                    catch (NotSupportedException) { }

                    // The count is of the matches; the step acts on at most its limit ("the latest" is one), and a reply,
                    // reply-all or forward answers one mail.
                    if (count is { } matched && query.Limit > 0) count = Math.Min(matched, query.Limit);
                    if (count is { } some && action == AgentAction.CreateReplyDraft) count = Math.Min(some, 1);
                }

                return (new StepTarget.Mails(mailScope, query), count);
            case AssistantTargets.Events:
                return await ResolveEventTargetAsync(step, action, item, scope, ct).ConfigureAwait(false);
            case AssistantTargets.Tasks:
                return await ResolveTaskTargetAsync(step, action, item, scope, ct).ConfigureAwait(false);
            case AssistantTargets.Contacts:
                return await ResolveContactTargetAsync(step, action, item, scope, ct).ConfigureAwait(false);
            case AssistantTargets.Folder:
                var folders = await ResolveContainersAsync(step, action, "folder", scope, true, ct).ConfigureAwait(false);
                if (folders.Count == 0 && item?.Props?.WellKnown is { } wk)
                    folders = WellKnownContainers(wk, scope.Ids).ToList();
                if (folders.Count == 0 && item is not null) folders = await ResolveContainersAsync(step with { Args = [.. step.Args, new AssistantArg("folder", item.Id)] }, action, "folder", scope, true, ct).ConfigureAwait(false);
                if (folders.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, step.Gloss);
                if (folders.Count > 1)
                {
                    var answer = Consume(step.N, "folder", ClarificationKind.PickContainer, step.Gloss ?? "folder", folders.Select(f => ContainerToCandidate(f, 0.5, false)).ToList(), searchable: false);
                    folders = answer.Refs.Select(Find).OfType<DomainIndex.Container>().ToList();
                    if (folders.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, step.Gloss);
                }

                return (new StepTarget.Folder(folders[0].AccountId, folders[0].Id), null);
            case AssistantTargets.List:
                var lists = await ResolveContainersAsync(step, action, "list", scope, true, ct).ConfigureAwait(false);
                if (lists.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, step.Gloss);
                return (new StepTarget.TaskListTarget(lists[0].AccountId, lists[0].Id), null);
            default:
                return (StepTarget.None.Instance, null);
        }
    }

    private bool SelectionKindMatches(ItemKind kind) => _t.Request.Selection?.Kind switch
    {
        AssistantSelectionKind.Mail => kind is ItemKind.Mail or ItemKind.Draft,
        AssistantSelectionKind.Event => kind == ItemKind.Event,
        AssistantSelectionKind.Task => kind == ItemKind.Task,
        AssistantSelectionKind.Contact => kind == ItemKind.Contact,
        AssistantSelectionKind.Folder => kind == ItemKind.Folder,
        _ => false,
    };

    private async Task<MailQuery> BuildMailQueryAsync(AssistantStep step, AgentAction action, AssistantRef? item, AccountScope scope, CancellationToken ct)
    {
        var q = MailQuery.Empty;
        // Folders: the folder argument, else the item's well-known folder, else every folder.
        var folders = await ResolveContainersAsync(step, action, "folder", scope, false, ct).ConfigureAwait(false);
        if (folders.Count == 0 && item?.Props?.WellKnown is { } wk) folders = WellKnownContainers(wk, scope.Ids).ToList();
        if (folders.Count == 0 && item is { Kind: AssistantRefKinds.Container }) folders = await ResolveContainersAsync(step with { Args = [.. step.Args, new AssistantArg("folder", item.Id)] }, action, "folder", scope, false, ct).ConfigureAwait(false);
        if (folders.Count == 0 && step.N == 1 && Refs(step, "folder").Any() == false)
        {
            // A container ref the classifier left unattached ("show Personal" with the folder beside an empty item ref).
            var orphans = OrphanRefs(AssistantRefKinds.Container).Where(r => r.By is AssistantRefBy.Name or AssistantRefBy.Description && KindOfType(r.Props?.Type) is null or ItemKind.Folder).ToList();
            if (orphans.Count > 0) folders = await ResolveContainersAsync(step with { Args = [.. step.Args, .. orphans.Select(o => new AssistantArg("folder", o.Id))] }, action, "folder", scope, false, ct).ConfigureAwait(false);
        }
        if (folders.Count == 0 && item is { Kind: AssistantRefKinds.Unknown } u && (u.Kinds ?? []).Contains(AssistantRefKinds.Container))
        {
            var picked = _t.Answers.ContainsKey($"{step.N}:account") || (u.Kinds ?? []).Contains(AssistantRefKinds.Account) ? null : await ResolveUnknownAsync(step, u, "on", ct).ConfigureAwait(false);
            if (picked is { Kind: ItemKind.Folder } pf && Find(pf) is { } found) folders = [found];
        }

        // The item's own category / container words when the classifier folded them into the item.
        if (folders.Count == 0 && item is not null && action is AgentAction.SearchMail && scope.One is { } one && _index.Containers.FirstOrDefault(c => c.AccountId == one && c.Kind == ItemKind.Folder && TextMatch.Same(c.Name, item.Norm)) is { } named)
            folders = [named];
        q = q with { FolderIds = folders.Where(f => f.Kind == ItemKind.Folder).Select(f => f.Id).ToList(), AllFolders = folders.Count == 0 };

        // Senders: person or org refs; the words stand in when nothing resolves.
        var senderRefs = Refs(step, "sender").ToList();
        if (senderRefs.Count == 0 && step.N == 1)
            senderRefs = R.Refs.Where(r => r.Role == AssistantRoles.Sender && !R.Steps.Any(s => s.Args.Any(a => a.Ref == r.Id || a.Refs?.Contains(r.Id) == true))).ToList();
        var addresses = new List<string>();
        var domains = new List<string>();
        var words = new List<string>();
        var i = 0;
        foreach (var r in senderRefs.SelectMany(Expand))
        {
            var field = $"sender[{i++}]";
            if (r.Kind == AssistantRefKinds.Org)
            {
                var org = await ResolveOrgFilterAsync(step, r, field, ct).ConfigureAwait(false);
                addresses.AddRange(org.Addresses);
                domains.AddRange(org.Domains);
                if (org.Words is not null) words.Add(org.Words);
            }
            else
            {
                var people = await ResolvePersonAsync(step, r, field, PersonUse.Filter, ct, scope.One).ConfigureAwait(false);
                addresses.AddRange(people.People.Select(p => p.Address));
                if (people.Words is not null) words.Add(people.Words);
            }
        }

        // A stated label may name a domain rather than an address.
        domains.AddRange(addresses.Where(TextMatch.LooksLikeDomain));
        addresses.RemoveAll(TextMatch.LooksLikeDomain);
        if (addresses.Count > 0) AddPeople(AssistantRoles.Sender, addresses.Select(a => new Recipient(a, null)));
        var recipients = await ResolvePeopleAsync(step, "recipient", PersonUse.Filter, ct).ConfigureAwait(false);
        var (from, to) = Period(step, "period", item);
        var flags = MailStateFlags.Any;
        foreach (var s in item?.Props?.State ?? [])
        {
            var f = TextMatch.Fold(s);
            if (f == "unread") flags |= MailStateFlags.Unread;
            else if (f == "read") flags |= MailStateFlags.Read;
            else if (f.StartsWith("flag") || f.StartsWith("star")) flags |= MailStateFlags.Flagged;
            else if (f.Contains("attach")) flags |= MailStateFlags.HasAttachments;
            else if (f == "important") flags |= MailStateFlags.Important;
            else if (f == "draft") flags |= MailStateFlags.Draft;
        }

        var senderKind = SenderKindOf(item?.Props?.ItemType);
        if (senderKind == SenderKind.Any && item is not null) senderKind = FilterWordKind(item);
        var text = item?.Props?.Topic ?? item?.Props?.Thread;
        var limit = LimitOf(step.Scope);
        if (limit == 0 && step.Scope is null && !ActionCatalog.IsFind(action) && action is AgentAction.Summarize or AgentAction.Translate or AgentAction.CopyCode or AgentAction.CreateReplyDraft or AgentAction.Unsubscribe) limit = 1;
        return q with
        {
            SenderAddresses = addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SenderDomains = domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SenderWords = words.Count == 0 ? null : string.Join(" ", words),
            RecipientAddresses = recipients.People.Select(p => p.Address).ToList(),
            Text = text,
            ItemType = item?.Props?.ItemType,
            Flags = flags,
            SenderKind = senderKind,
            From = from,
            To = to,
            Limit = limit,
            Order = step.Scope == AssistantScopes.Oldest ? ResultOrder.Oldest : ResultOrder.Newest,
            Negated = step.Neg,
        };
    }

    /// <summary>A canonical item type names a sender kind the app's classifier knows; a stated filter word maps the user's own words.</summary>
    private SenderKind SenderKindOf(string? itemType)
    {
        if (itemType is null) return SenderKind.Any;
        var f = TextMatch.Fold(itemType);
        foreach (var fw in _brain.Find(Brain.BrainEntryKind.FilterWord).Where(e => TextMatch.Same(e.Subject.Text, itemType)))
            if (Enum.TryParse<SenderKind>(fw.Value.Split('=').Last(), true, out var sk)) return sk;
        return f switch
        {
            "newsletter" or "newsletters" or "promotion" or "promotions" => SenderKind.Newsletter,
            "notification" or "notifications" or "service" or "automated" => SenderKind.Service,
            "personal" or "person" or "people" => SenderKind.Person,
            _ => SenderKind.Any,
        };
    }

    /// <summary>A stated filter word ("promos" = newsletters) matching the item's words.</summary>
    private SenderKind FilterWordKind(AssistantRef item)
    {
        foreach (var fw in _brain.Find(Brain.BrainEntryKind.FilterWord))
        {
            var words = fw.Subject.Text ?? "";
            if (TextMatch.Same(words, item.Text) || TextMatch.Same(words, item.Norm) || (TextMatch.Key(words).Length >= 4 && TextMatch.Quoted(item.Text, words)))
                if (Enum.TryParse<SenderKind>(fw.Value.Split('=').Last(), true, out var sk)) return sk;
        }

        return SenderKind.Any;
    }

    private async Task<(StepTarget, int?)> ResolveEventTargetAsync(AssistantStep step, AgentAction action, AssistantRef? item, AccountScope scope, CancellationToken ct)
    {
        var calendars = await ResolveContainersAsync(step, action, "calendar", scope, false, ct).ConfigureAwait(false);
        var attendees = await ResolvePeopleAsync(step, "attendee", PersonUse.Filter, ct).ConfigureAwait(false);
        var (from, to) = Period(step, "when", item);
        var relTo = item?.Time?.RelTo is { } rel && Ref(rel) is { } relRef ? relRef : null;
        var title = item?.Props?.Topic ?? (item is { Kind: AssistantRefKinds.Item } && item.Props?.WellKnown is null && item.Time is null ? item.Norm ?? item.Text : null);
        if (title is not null && attendees.Words is not null && TextMatch.Same(title, attendees.Words)) title = null;
        var limit = LimitOf(step.Scope);
        var all = step.Scope == AssistantScopes.All;
        var query = new EventQuery(calendars.Select(c => c.Id).ToList(), title, attendees.People.Select(a => a.Address).ToList(), attendees.Words, from, to, limit, all);
        var accountId = scope.One ?? (calendars.Count > 0 && calendars.Select(c => c.AccountId).Distinct().Count() == 1 ? calendars[0].AccountId : null);
        if (ActionCatalog.IsFind(action) || all || limit > 1) return (new StepTarget.Events(accountId, query), null);
        // An action on existing events without "all": one event, chosen when several match.
        var matches = await _q.FindEventsAsync(accountId, query, 10, ct).ConfigureAwait(false);
        if (matches.Count == 0) return (new StepTarget.Events(accountId, query with { Limit = 1 }), 0);
        // A change to "the dentist" with no day said means one that has not happened yet: past occurrences do not compete,
        // a single upcoming one is taken, and among several the nearest is suggested first.
        if (from is null && to is null && matches.Count > 1 && matches.Where(m => m.End > Now).OrderBy(m => m.Start).ToList() is { Count: > 0 } upcoming)
            matches = upcoming;
        if (matches.Count == 1) return (new StepTarget.EventItems([new ItemRef(ItemKind.Event, matches[0].AccountId, matches[0].Id.ToString())]), 1);
        var options = matches.Select(m => new Candidate(new ItemRef(ItemKind.Event, m.AccountId, m.Id.ToString()), m.Title, m.AllDay ? m.Start.ToString("yyyy-MM-dd") : m.Start.ToString("yyyy-MM-dd HH:mm"), _index.AccountName(m.AccountId), 0.5, false)).ToList();
        var answer = Consume(step.N, "event", ClarificationKind.ChooseItem, item?.Text ?? step.Gloss ?? "event", options, many: false, searchable: false, suggested: from is null && to is null ? options[0] : null);
        var chosen = answer.Refs.Where(r => r.Kind == ItemKind.Event).ToList();
        if (chosen.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, step.Gloss);
        return (new StepTarget.EventItems(chosen), chosen.Count);
    }

    private async Task<(StepTarget, int?)> ResolveTaskTargetAsync(AssistantStep step, AgentAction action, AssistantRef? item, AccountScope scope, CancellationToken ct)
    {
        var lists = await ResolveContainersAsync(step, action, "list", scope, false, ct).ConfigureAwait(false);
        var (from, to) = Period(step, "due", item);
        var states = (item?.Props?.State ?? []).Select(TextMatch.Fold).ToList();
        var title = item?.Props?.Topic ?? (item is { Kind: AssistantRefKinds.Item } && item.Time is null ? item.Norm ?? item.Text : null);
        var limit = LimitOf(step.Scope);
        var all = step.Scope == AssistantScopes.All;
        var query = new TaskQuery(lists.Select(l => l.Id).ToList(), title, from, to,
            step.Literal("overdue") == "true" || states.Contains("overdue"),
            step.Literal("important") == "true" || states.Contains("important"),
            step.Literal("myDay") == "true",
            step.Literal("completed") == "true" || states.Any(s => s.StartsWith("complet") || s == "done"),
            limit, all);
        var accountId = scope.One ?? (lists.Count > 0 && lists.Select(c => c.AccountId).Distinct().Count() == 1 ? lists[0].AccountId : null);
        if (ActionCatalog.IsFind(action) || all || limit > 1) return (new StepTarget.Tasks(accountId, query), null);
        var matches = await _q.FindTasksAsync(accountId, query, 10, ct).ConfigureAwait(false);
        if (matches.Count == 0) return (new StepTarget.Tasks(accountId, query with { Limit = 1 }), 0);
        if (matches.Count == 1) return (new StepTarget.TaskItems([new ItemRef(ItemKind.Task, matches[0].AccountId, matches[0].Id.ToString())]), 1);
        var options = matches.Select(m => new Candidate(new ItemRef(ItemKind.Task, m.AccountId, m.Id.ToString()), m.Title, m.Due?.ToString("yyyy-MM-dd"), _index.AccountName(m.AccountId), 0.5, false)).ToList();
        var answer = Consume(step.N, "task", ClarificationKind.ChooseItem, item?.Text ?? step.Gloss ?? "task", options, searchable: false);
        var chosen = answer.Refs.Where(r => r.Kind == ItemKind.Task).ToList();
        if (chosen.Count == 0) throw new TurnMessageException(AgentMessageKind.NothingMatched, step.Gloss);
        return (new StepTarget.TaskItems(chosen), chosen.Count);
    }

    private async Task<(StepTarget, int?)> ResolveContactTargetAsync(AssistantStep step, AgentAction action, AssistantRef? item, AccountScope scope, CancellationToken ct)
    {
        var people = await ResolvePeopleAsync(step, "person", action == AgentAction.FindContacts ? PersonUse.Filter : PersonUse.Recipient, ct).ConfigureAwait(false);
        if (people.People.Count > 0 && !ActionCatalog.IsFind(action))
            return (new StepTarget.ContactItems(people.People.Select(p => ItemRef.Contact(p.Address)).ToList()), people.People.Count);
        var list = (await ResolveContainersAsync(step, action, "list", AccountScope.All, true, ct).ConfigureAwait(false)).FirstOrDefault();
        var category = (await ResolveContainersAsync(step, action, "category", scope, true, ct).ConfigureAwait(false)).FirstOrDefault();
        var query = new ContactQuery(people.Words ?? item?.Props?.Topic ?? item?.Norm, people.People.Select(p => p.Address).ToList(), list?.Id, category?.Id, step.Literal("favorites") == "true", LimitOf(step.Scope));
        return (new StepTarget.Contacts(query), null);
    }

    // ---- arguments -----------------------------------------------------------------------------------------------

    private async Task<StepArgs> ResolveArgsAsync(AssistantStep step, AgentAction action, AccountScope scope, StepTarget target, CancellationToken ct)
    {
        var info = ActionCatalog.Info(action);
        switch (info.Family)
        {
            case "draft":
                return await ResolveDraftArgsAsync(step, action, scope, ct).ConfigureAwait(false);
            case "mail":
                if (action == AgentAction.CreateReplyDraft) return await ResolveDraftArgsAsync(step, action, scope, ct).ConfigureAwait(false);
                var destination = action == AgentAction.MoveToFolder ? (await ResolveContainersAsync(step, action, "destination", scope, true, ct).ConfigureAwait(false)).FirstOrDefault() : null;
                var category = action is AgentAction.Categorize or AgentAction.Uncategorize ? (await ResolveContainersAsync(step, action, "category", scope, true, ct).ConfigureAwait(false)).FirstOrDefault() : null;
                if (action == AgentAction.MoveToFolder && destination is null) throw new TurnMessageException(AgentMessageKind.NothingMatched, step.Gloss);
                return new StepArgs.MailChangeArgs(destination?.Id, category?.Id, step.Literal("language"), step.Literal("always") == "true");
            case "calendar":
                if (action == AgentAction.CreateEvent) return await ResolveEventArgsAsync(step, scope, ct).ConfigureAwait(false);
                return await ResolveEventChangeArgsAsync(step, action, scope, ct).ConfigureAwait(false);
            case "task":
                if (action == AgentAction.CreateTask) return await ResolveTaskArgsAsync(step, scope, ct).ConfigureAwait(false);
                if (action == AgentAction.CreateTaskList) return new StepArgs.FolderArgs((scope.One ?? DefaultAccount(step, "task", step.Gloss ?? "list")?.Id), ContentText(Refs(step, "name").FirstOrDefault()));
                if (action == AgentAction.RenameTaskList) return new StepArgs.FolderArgs(null, null, ContentText(Refs(step, "newName").FirstOrDefault()));
                var toList = (await ResolveContainersAsync(step, action, "toList", scope, true, ct).ConfigureAwait(false)).FirstOrDefault();
                var newDue = TimeOf(step, "newDue");
                return new StepArgs.TaskChangeArgs(ContentText(Refs(step, "newTitle").FirstOrDefault()), ContentText(Refs(step, "notes").FirstOrDefault()), newDue.Start,
                    step.Literal("clearDue") == "true", Bool(step.Literal("setImportant")), Bool(step.Literal("setMyDay")), toList?.Id, ContentText(Refs(step, "step").FirstOrDefault()));
            case "contact":
                // Contact lists are not per account; a contact's categories are its account's (the same table the mails use),
                // so a category is looked up in the account the contacts belong to (the selection's) rather than in every account.
                var list = (await ResolveContainersAsync(step, action, "list", AccountScope.All, true, ct).ConfigureAwait(false)).FirstOrDefault();
                var ccat = (await ResolveContainersAsync(step, action, "category", scope, true, ct).ConfigureAwait(false)).FirstOrDefault();
                return new StepArgs.ContactChangeArgs(list?.Id, ccat?.Id, ContentText(Refs(step, "name").FirstOrDefault()), step.Literal("email"), step.Literal("phone"));
            case "folder":
                var parent = action == AgentAction.CreateFolder ? (await ResolveContainersAsync(step, action, "parent", scope, true, ct).ConfigureAwait(false)).FirstOrDefault() : null;
                var folderAccount = scope.One ?? parent?.AccountId ?? (action == AgentAction.CreateFolder ? DefaultAccount(step, "mail", step.Gloss ?? "folder")?.Id : null);
                return new StepArgs.FolderArgs(folderAccount, ContentText(Refs(step, "name").FirstOrDefault()), ContentText(Refs(step, "newName").FirstOrDefault()), parent?.Id);
            case "rule":
                return await ResolveMailRuleArgsAsync(step, action, scope, ct).ConfigureAwait(false);
            default:
                if (action == AgentAction.ChangeSetting && Enum.TryParse<AgentSetting>(step.Literal("setting"), true, out var setting))
                    return new StepArgs.SettingArgs(setting, step.Literal("enabled") == "true");
                if (action == AgentAction.OpenView && Enum.TryParse<AgentView>(step.Literal("view"), true, out var view))
                    return new StepArgs.ViewArgs(view, scope.One);
                return new StepArgs.SystemArgs(scope.One);
        }
    }

    private static bool? Bool(string? literal) => literal is null ? null : literal.Equals("true", StringComparison.OrdinalIgnoreCase);

    private async Task<StepArgs> ResolveDraftArgsAsync(AssistantStep step, AgentAction action, AccountScope scope, CancellationToken ct)
    {
        Guid? accountId = scope.One;
        if (accountId is null && action == AgentAction.CreateDraft)
        {
            var account = DefaultAccount(step, "mail", step.Gloss ?? "draft");
            accountId = account?.Id;
        }

        var to = await ResolvePeopleAsync(step, "to", PersonUse.Recipient, ct, accountId: accountId).ConfigureAwait(false);
        var cc = await ResolvePeopleAsync(step, "cc", PersonUse.Recipient, ct, accountId: accountId).ConfigureAwait(false);
        var bcc = await ResolvePeopleAsync(step, "bcc", PersonUse.Recipient, ct, accountId: accountId).ConfigureAwait(false);
        if (to.People.Count == 0 && action == AgentAction.CreateDraft && step.N == 1)
        {
            // Orphan person refs with role "to" the classifier did not attach.
            var orphans = R.Refs.Where(r => r.Kind is AssistantRefKinds.Person or AssistantRefKinds.Group && r.Role is AssistantRoles.To or null && !R.Steps.Any(s => s.Args.Any(a => a.Ref == r.Id || a.Refs?.Contains(r.Id) == true)) && !R.Refs.Any(x => x.Members?.Contains(r.Id) == true)).ToList();
            if (orphans.Count > 0) to = await ResolvePeopleAsync(step, "to", PersonUse.Recipient, ct, orphans, accountId).ConfigureAwait(false);
        }

        var subjectRef = Refs(step, "subject").FirstOrDefault();
        var bodyRef = Refs(step, "body").FirstOrDefault();
        var kind = action == AgentAction.CreateReplyDraft ? (Enum.TryParse<ReplyKind>(step.Literal("kind"), true, out var rk) ? rk : ReplyKind.Reply) : (ReplyKind?)null;
        DraftBody? body = null;
        if (bodyRef is not null)
        {
            if (bodyRef.Content?.Mode == AssistantRefBy.Composed && bodyRef.Content.Points is { Count: > 0 } points)
            {
                body = new DraftBody(string.Join("\n", points), BodyKind.Points, bodyRef.Content.Language);
                _t.Composes.Add(new PendingCompose(step.N, "body", points, AssistantComposeKinds.Body, bodyRef.Content.Language, bodyRef.Content.Tone, bodyRef.Content.Length, to.People, accountId));
            }
            else if (ContentText(bodyRef) is { } text) body = new DraftBody(text, BodyKind.Dictated, bodyRef.Content?.Language);
        }

        // A draft is never sent by the runtime: "send" words on a CreateDraft are ignored (plan 4.6).
        return new StepArgs.DraftArgs(accountId, to.People, cc.People, bcc.People, ContentText(subjectRef), body, kind, bodyRef?.Content?.Language ?? R.Lang, bodyRef?.Content?.Tone, Send: false);
    }

    private async Task<StepArgs> ResolveEventArgsAsync(AssistantStep step, AccountScope scope, CancellationToken ct)
    {
        var calendar = await ResolveCalendarForNewEventAsync(step, scope, ct).ConfigureAwait(false);
        var accountId = calendar?.AccountId ?? scope.One;
        var attendees = await ResolvePeopleAsync(step, "attendees", PersonUse.Recipient, ct, accountId: accountId).ConfigureAwait(false);
        if (attendees.People.Count == 0 && step.N == 1)
        {
            // Orphan person refs with the attendee role the classifier did not attach.
            var orphans = R.Refs.Where(r => r.Kind is AssistantRefKinds.Person or AssistantRefKinds.Group && r.Role == AssistantRoles.Attendee && !R.Steps.Any(s => s.Args.Any(a => a.Ref == r.Id || a.Refs?.Contains(r.Id) == true)) && !R.Refs.Any(x => x.Members?.Contains(r.Id) == true)).ToList();
            if (orphans.Count > 0) attendees = await ResolvePeopleAsync(step, "attendees", PersonUse.Recipient, ct, orphans, accountId).ConfigureAwait(false);
        }

        var startRef = Refs(step, "start").FirstOrDefault();
        var start = startRef is null ? ResolvedTime.Empty : TimeResolver.Resolve(startRef, _clock);
        var slotRef = Refs(step, "slot").FirstOrDefault();
        SlotRequest? slot = null;
        if (slotRef is not null)
        {
            var window = TimeResolver.Resolve(slotRef, _clock);
            var from = window.Start ?? Now;
            var to = window.End ?? from.Date.AddDays(7);
            var minutes = slotRef.Time?.OffsetMinutes is { } om && om > 0 ? om : window.Start is { } ws && window.End is { } we && (we - ws).TotalHours <= 8 ? (int)(we - ws).TotalMinutes : 60;
            var latest = step.Scope == AssistantScopes.Latest || slotRef.Time?.Dir == "past";
            var request = new SlotRequest(from, to, minutes, latest, true, null);
            var busy = await _q.GetBusyIntervalsAsync(accountId, from, to, ct).ConfigureAwait(false);
            var found = TimeResolver.FindFreeSlot(request, busy, Now, _clock.WorkDayStart, _clock.WorkDayEnd);
            slot = request with { Suggested = found };
            if (found is { } f && start.Start is null) start = new ResolvedTime(f, f.AddMinutes(minutes), false, ["time"], null, false, null);
        }

        if (startRef is not null && start.Vague)
        {
            var suggestion = TimeResolver.RoundUp(Now.AddMinutes(30));
            var option = new Candidate(new ItemRef(ItemKind.Time, null, suggestion.ToString("s")), suggestion.ToString("yyyy-MM-dd HH:mm"), null, null, 0.5, false);
            var answer = Consume(step.N, "start", ClarificationKind.ConfirmTime, SaidAs(startRef), [option], searchable: false, suggested: option);
            var chosen = answer.Refs.FirstOrDefault(r => r.Kind == ItemKind.Time);
            var at = chosen is null ? suggestion : TimeResolver.Parse(chosen.Id) ?? suggestion;
            start = new ResolvedTime(at, at.AddHours(1), false, ["duration"], null, false, null);
        }

        if (start.Start is { } s && s < Now.AddDays(-1) && startRef?.Time?.Dir != "past" && !start.AllDay)
        {
            // A new event in the past is a classifier slip: confirm the time.
            var option = new Candidate(new ItemRef(ItemKind.Time, null, s.ToString("s")), s.ToString("yyyy-MM-dd HH:mm"), null, null, 0.5, false);
            var answer = Consume(step.N, "start", ClarificationKind.ConfirmTime, SaidAs(startRef!), [option], searchable: false, suggested: option);
            if (answer.Refs.FirstOrDefault(r => r.Kind == ItemKind.Time) is { } chosen && TimeResolver.Parse(chosen.Id) is { } at)
                start = start with { Start = at, End = at + ((start.End ?? s.AddHours(1)) - s) };
        }

        return new StepArgs.EventArgs(calendar?.Id, ContentText(Refs(step, "title").FirstOrDefault()), start.Start, start.End, start.AllDay, attendees.People,
            ContentText(Refs(step, "location").FirstOrDefault()), ContentText(Refs(step, "notes").FirstOrDefault()), step.Literal("showAs"), slot, start.Assumed);
    }

    private async Task<StepArgs> ResolveEventChangeArgsAsync(AssistantStep step, AgentAction action, AccountScope scope, CancellationToken ct)
    {
        var newDate = TimeOf(step, "newDate");
        var newStart = TimeOf(step, "newStart");
        var messageRef = Refs(step, "message").FirstOrDefault();
        string? message = ContentText(messageRef);
        if (messageRef?.Content?.Mode == AssistantRefBy.Composed && messageRef.Content.Points is { Count: > 0 } points)
            _t.Composes.Add(new PendingCompose(step.N, "message", points, AssistantComposeKinds.ResponseMessage, messageRef.Content.Language, messageRef.Content.Tone, messageRef.Content.Length, [], scope.One));
        SlotRequest? slot = null;
        if (Refs(step, "slot").FirstOrDefault() is { } slotRef)
        {
            var window = TimeResolver.Resolve(slotRef, _clock);
            var from = window.Start ?? Now;
            var to = window.End ?? from.Date.AddDays(7);
            var minutes = step.Literal("durationMinutes") is { } dm && int.TryParse(dm, out var d) ? d : 60;
            var request = new SlotRequest(from, to, minutes, false, true, null);
            var busy = await _q.GetBusyIntervalsAsync(scope.One, from, to, ct).ConfigureAwait(false);
            slot = request with { Suggested = TimeResolver.FindFreeSlot(request, busy, Now, _clock.WorkDayStart, _clock.WorkDayEnd) };
        }

        return new StepArgs.EventChangeArgs(newDate.Start, newStart.Start, Int(step.Literal("shiftMinutes")), Int(step.Literal("durationMinutes")),
            ContentText(Refs(step, "newTitle").FirstOrDefault()), ContentText(Refs(step, "newLocation").FirstOrDefault()), step.Literal("showAs"), step.Literal("response"),
            message, Int(step.Literal("reminderMinutes")), step.Literal("series") == "true", slot);
    }

    private static int? Int(string? literal) => literal is not null && int.TryParse(literal, out var n) ? n : null;

    private async Task<StepArgs> ResolveTaskArgsAsync(AssistantStep step, AccountScope scope, CancellationToken ct)
    {
        var list = await ResolveListForNewTaskAsync(step, scope, ct).ConfigureAwait(false);
        var due = TimeOf(step, "due");
        return new StepArgs.TaskArgs(list?.Id, ContentText(Refs(step, "title").FirstOrDefault()), due.Start, step.Literal("important") == "true", step.Literal("myDay") == "true", ContentText(Refs(step, "notes").FirstOrDefault()));
    }
}
