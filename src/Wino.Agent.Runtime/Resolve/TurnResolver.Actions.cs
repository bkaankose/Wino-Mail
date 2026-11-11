using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Ports;
using Wino.Agent.Runtime.Reading;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

/// <summary>
/// Which action a step is. The reader guesses the family (a mail, a task, an event, a contact, a folder) from words
/// alone; the device decides. The candidates are the primary, the reader's alternatives and the catalog's equivalents
/// (the same operation on another kind), each rewritten so its refs sit in that action's slots. A candidate is kept
/// when the thing it needs exists on the device with the kind it needs. The primary stands when its own thing exists
/// and the reader rated no alternative above it; otherwise one survivor is taken without a question, several are
/// ranked by the reader's confidence and asked about only when they are close and one is destructive, outgoing or
/// aimed at a different thing. A destructive or outgoing survivor the reader did not put first never runs unasked:
/// it is asked about or the step is confirmed. Nothing here looks at words: only at the reading's structure and the
/// device's answers.
/// </summary>
public sealed partial class TurnResolver
{
    private sealed record ActionCandidate(AgentAction Action, AssistantStep Step, double Conf, bool Primary, bool FromReader)
    {
        public string Name => Action.ToString();
    }

    private readonly Dictionary<string, bool> _exists = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentEventSummary?> _eventByTitle = new(StringComparer.Ordinal);

    private async Task<(AgentAction Action, AssistantStep Step)> ChooseActionAsync(AssistantStep step, CancellationToken ct)
    {
        var action = ReadingValidator.ToAction(step.Action);
        // A reply needs a mail to answer: "reply to X that..." with no mail named (no on, from, sender or selection) answers the latest mail from X, so X is the sender filter, not a recipient.
        // With several people there is no one mail from all of them: the words are a new mail to all of them, and nobody is dropped.
        if (action == AgentAction.CreateReplyDraft && step.On is null && step.From is null && _t.Request.Selection is null && step.Arg("to") is { } to && (to.Ref is not null || to.Refs is { Count: > 0 })
            && !Refs(step, "sender").Any() && (step.Literal("kind") is null || string.Equals(step.Literal("kind"), nameof(ReplyKind.Reply), StringComparison.OrdinalIgnoreCase)))
        {
            var recipients = Refs(step, "to").ToList();
            var several = recipients.Any(r => r.Kind == AssistantRefKinds.Group) || recipients.SelectMany(Expand).Count() > 1;
            step = several
                ? step with { Action = AssistantActions.CreateDraft, Args = step.Args.Where(a => a.Name != "kind").ToList() }
                : step with { Args = [.. step.Args.Where(a => a.Name != "to"), new AssistantArg("sender", to.Ref, to.Refs)] };
        }

        step = CanonicalizeCompletion(step);
        action = ReadingValidator.ToAction(step.Action);
        var candidates = BuildCandidates(step, action);
        if (_t.Answers.ContainsKey($"{step.N}:action"))
        {
            var answer = Consume(step.N, "action", ClarificationKind.ChooseAction, step.Gloss ?? step.Action, candidates.Select(ToOption).ToList(), searchable: false);
            var picked = answer.Refs.Select(r => candidates.FirstOrDefault(c => c.Name == r.Id)).FirstOrDefault(c => c is not null);
            return picked is null ? (action, step) : (picked.Action, picked.Step);
        }

        if (candidates.Count == 1) return (action, step);
        var primary = candidates[0];
        var primaryEvidence = await ExistsAsync(primary, ct).ConfigureAwait(false);
        // A create beside a reader alternative that acts on existing items ("Combo at 7 from now on": a new event or the series;
        // "remind me before the dentist": a task or the event's reminder): the device says whether the named thing is there.
        var pair = candidates.FirstOrDefault(c => !c.Primary && c.FromReader && (IsCreateUpdatePair(primary.Action, c.Action)
            || (ActionCatalog.Creates(primary.Action) && ActionCatalog.ItemKindOf(c.Action) is ItemKind.Event or ItemKind.Task && !ActionCatalog.IsFind(c.Action))));
        var higher = candidates.Any(c => !c.Primary && c.FromReader && c.Conf > primary.Conf);
        if (primaryEvidence.Stands && !higher && step.Conf >= 0.6 && pair is null) return (action, step);

        var alive = new List<(ActionCandidate Candidate, Evidence Evidence)>();
        if (primaryEvidence.Stands) alive.Add((primary, primaryEvidence));
        foreach (var c in candidates.Skip(1))
        {
            var evidence = await ExistsAsync(c, ct).ConfigureAwait(false);
            if (evidence.Stands) alive.Add((c, evidence));
        }

        // "add X to my day" with no task X: the update has nothing to change, the create is what the words carry.
        if (primaryEvidence.Status == Existence.Missing && primary.Action == AgentAction.UpdateTask && alive.FirstOrDefault(x => x.Candidate.Action == AgentAction.CreateTask) is { Candidate: not null } create)
            return (create.Candidate.Action, create.Candidate.Step);
        // A thing the device merely lacks (or holds under another role) is not evidence for another reading: a primary that is missing
        // its thing yields only to a candidate whose own thing or container the device found. A primary that cannot stand at all (a
        // container where a mail is needed, no folder for a folder action) yields to any candidate that stands.
        var primaryUnsure = primaryEvidence.Status is Existence.Missing or Existence.WrongRole;
        if (primaryUnsure) alive.RemoveAll(x => !x.Evidence.Found);
        // "Combo at 7 from now on" with a Combo series: the reader offered an action on the thing and the device has it. Both readings
        // stand, so ask; the action on the found thing is suggested, except a plain move of a one-off event (a new event is as likely).
        if (pair is not null && ActionCatalog.Creates(primary.Action))
        {
            if (alive.FirstOrDefault(x => x.Candidate == pair) is { Candidate: not null, Evidence.Found: true })
            {
                // A move of an existing event is suggested only for a series or when the create's date was the reader's assumption; any other action on the found thing is suggested.
                var suggestAlt = pair.Action != AgentAction.UpdateEvent || pair.Step.Literal("series") == "true" || (Thing(pair.Step) is { } thing && (await FirstEventAsync(thing, ct).ConfigureAwait(false))?.Recurring == true);
                return Ask(step, [primary, pair], suggested: suggestAlt ? pair : primary);
            }

            alive.RemoveAll(x => x.Candidate == pair);
        }

        var survivors = alive.Select(x => x.Candidate).OrderByDescending(c => c.Conf).ThenBy(c => c.Primary ? 0 : c.FromReader ? 1 : 2).ThenBy(c => candidates.IndexOf(c)).ToList();
        if (survivors.Count == 0) return (action, step);
        var top = survivors[0];
        if (top.Primary) return survivors.Count == 1 || step.Conf >= 0.6 ? (action, step) : Ask(step, survivors, suggested: top);
        // One reading left. When the primary could not stand at all, that reading is the device's word, not a guess. When the device
        // merely lacked the primary's thing, or holds it under a role the primary cannot act on (someone else's event is declined,
        // not deleted), and the survivor is destructive or outgoing, the change of reading is asked about first.
        if (survivors.Count == 1) return primaryUnsure && ActionCatalog.IsDestructiveOrOutgoing(top.Action) ? Ask(step, [top, primary], suggested: top) : Take(step, top);
        var runnerUp = primaryEvidence.Stands ? primary.Conf : survivors[1].Conf;
        var clear = top.Conf - runnerUp >= 0.2;
        if (clear && (!ActionCatalog.IsDestructiveOrOutgoing(top.Action) || (primaryEvidence.Stands && SameThing(top.Step, primary.Step)))) return Take(step, top);
        return Ask(step, survivors, suggested: top);
    }

    /// <summary>
    /// Takes a candidate without a question. A destructive or outgoing one the reader did not put first never runs on the
    /// device's word alone, whatever its risk: the step is marked for confirmation (an answered question needs no mark).
    /// </summary>
    private (AgentAction, AssistantStep) Take(AssistantStep step, ActionCandidate c)
    {
        if (!c.Primary && ActionCatalog.IsDestructiveOrOutgoing(c.Action)) _switchedRisky.Add(step.N);
        return (c.Action, c.Step);
    }

    private (AgentAction, AssistantStep) Ask(AssistantStep step, IReadOnlyList<ActionCandidate> options, ActionCandidate suggested)
    {
        var shown = options.Select(ToOption).ToList();
        var answer = Consume(step.N, "action", ClarificationKind.ChooseAction, step.Gloss ?? step.Action, shown, searchable: false, suggested: ToOption(suggested));
        var picked = answer.Refs.Select(r => options.FirstOrDefault(c => c.Name == r.Id)).FirstOrDefault(c => c is not null) ?? suggested;
        return (picked.Action, picked.Step);
    }

    private static Candidate ToOption(ActionCandidate c) => new(new ItemRef(ItemKind.Action, null, c.Name), c.Name, null, null, c.Conf, false);

    private static bool IsCreateUpdatePair(AgentAction a, AgentAction b)
        => (a, b) is (AgentAction.CreateTask, AgentAction.UpdateTask) or (AgentAction.UpdateTask, AgentAction.CreateTask)
            or (AgentAction.CreateEvent, AgentAction.UpdateEvent) or (AgentAction.UpdateEvent, AgentAction.CreateEvent);

    /// <summary>An update whose only change is the completion state is the completion action itself (the reader may also offer it as the alternative).</summary>
    private static AssistantStep CanonicalizeCompletion(AssistantStep step)
    {
        if (step.Action != AssistantActions.UpdateTask || step.Args.Any(a => ActionCatalog.TaskChangeArgs.Contains(a.Name))) return step;
        var completed = step.Literal("completed");
        var offered = (step.Alt ?? []).Where(a => a.Action is AssistantActions.CompleteTask or AssistantActions.ReopenTask).OrderByDescending(a => a.Conf).FirstOrDefault();
        var action = offered?.Action ?? (completed is null ? null : string.Equals(completed, "true", StringComparison.OrdinalIgnoreCase) ? AssistantActions.CompleteTask : AssistantActions.ReopenTask);
        if (action is null) return step;
        var args = step.Args.Where(a => a.Name != "completed").ToList();
        if (action == AssistantActions.ReopenTask) args.Add(new AssistantArg("completed", Value: "true"));
        return step with { Action = action, Args = args, Conf = Math.Max(step.Conf, offered?.Conf ?? 0), Alt = step.Alt?.Where(a => a.Action != action).ToList() };
    }

    /// <summary>The primary first, then the reader's alternatives in its order, then the catalog's equivalents of the primary that the refs can fill.</summary>
    private List<ActionCandidate> BuildCandidates(AssistantStep step, AgentAction action)
    {
        var list = new List<ActionCandidate> { new(action, step, step.Conf, true, true) };
        // A step a later step links from feeds that step: its action is settled by the chain, whatever the reader offered beside it.
        var feedsLater = R.Steps.Any(s => s.From?.Step == step.N);
        foreach (var alt in feedsLater ? [] : step.Alt ?? [])
        {
            if (!Enum.TryParse<AgentAction>(alt.Action, out var a) || list.Any(c => c.Action == a)) continue;
            if (Rewrite(step, action, a) is { } rewritten) list.Add(new ActionCandidate(a, rewritten, alt.Conf, false, true));
        }

        foreach (var a in Equivalents(step, action))
        {
            if (list.Any(c => c.Action == a)) continue;
            if (Rewrite(step, action, a) is { } rewritten) list.Add(new ActionCandidate(a, rewritten, step.Conf, false, false));
        }

        return list;
    }

    /// <summary>The catalog's equivalents of this step's action, narrowed by what the step carries (an update is a move when it names a list, a rename when it names a title).</summary>
    private static IEnumerable<AgentAction> Equivalents(AssistantStep step, AgentAction action)
    {
        var families = ActionCatalog.FamiliesOf(action).ToList();
        switch (action)
        {
            case AgentAction.UpdateTask:
                families = step.Arg("toList") is not null ? families.Where(f => f.Contains(AgentAction.MoveToFolder)).ToList()
                    : step.Arg("newTitle") is not null ? families.Where(f => f.Contains(AgentAction.RenameFolder)).ToList() : [];
                break;
            case AgentAction.UpdateEvent:
                families = step.Arg("newTitle") is not null ? families.Where(f => f.Contains(AgentAction.RenameFolder)).ToList()
                    : step.Arg("calendar") is not null && !step.Args.Any(a => ActionCatalog.EventChangeArgs.Contains(a.Name)) ? families.Where(f => f.Contains(AgentAction.MoveToFolder)).ToList() : [];
                break;
            case AgentAction.RespondToEvent:
                if (!string.Equals(step.Literal("response"), "Decline", StringComparison.OrdinalIgnoreCase)) families = [];
                break;
        }

        var result = new List<AgentAction>();
        foreach (var a in families.SelectMany(f => f).Where(a => a != action).Distinct())
        {
            // A mail reading cannot be checked by a name, so nothing falls back to it; only container-or-mail pairs go both ways.
            if (ActionCatalog.IsMailAction(a) && !ActionCatalog.IsFolderAction(action)) continue;
            // Nothing moves an event between calendars; the update only reaches the move family as a source.
            if (a == AgentAction.UpdateEvent && action != AgentAction.RenameFolder && action != AgentAction.RenameTaskList && step.Arg("newTitle") is null && step.Arg("newName") is null) continue;
            // A task update on the move or rename side needs the words of a task; the completion canonical form is handled before.
            if (a == AgentAction.UpdateTask && action is AgentAction.RespondToEvent or AgentAction.DeleteEvent) continue;
            result.Add(a);
        }

        // "add X to my day" names a task that may not exist: the update becomes a create when its only changes are flags a new task
        // takes (My Day, importance). A move, a new date or a new title names an existing task and stays an update.
        if (action == AgentAction.UpdateTask && step.Args.Where(a => ActionCatalog.TaskChangeArgs.Contains(a.Name)).All(CreateCanTake) && step.Literal("setMyDay") == "true")
            result.Add(AgentAction.CreateTask);
        return result;
    }

    private static bool CreateCanTake(AssistantArg a) => a.Name is "setMyDay" or "setImportant" && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase);

    // ---- rewriting -----------------------------------------------------------------------------------------------

    /// <summary>The item ref a step acts on, or the thing its container slot names when it acts on a container.</summary>
    private AssistantRef? Thing(AssistantStep step)
    {
        var action = ReadingValidator.ToAction(step.Action);
        if (step.On is { } on && on != AssistantStep.OnSelection) return Ref(on);
        if (step.On is null && step.From is null)
        {
            var target = ActionCatalog.Target(action);
            if (target == AssistantTargets.Folder) return Refs(step, "folder").FirstOrDefault();
            if (target == AssistantTargets.List) return Refs(step, "list").FirstOrDefault();
            if (ActionCatalog.Creates(action)) return Refs(step, "title").FirstOrDefault();
        }

        return step.On is null && step.N == 1 && step.From is null ? OrphanRefs(AssistantRefKinds.Item).FirstOrDefault() : null;
    }

    /// <summary>The words a thing is named by: its topic, its base form, the user's words, or the text it carries.</summary>
    private static IReadOnlyList<string> NamesOf(AssistantRef r)
        => new[] { r.Props?.Topic, r.Norm, r.Text, r.Content?.Text }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).DistinctBy(TextMatch.Key).Where(x => TextMatch.Key(x).Length > 0).ToList();

    /// <summary>
    /// The step as it reads for another action: same-named arguments carry over, then the thing, the container, the new
    /// name and the people move into the slots the other action has for them. Null when the action needs something the
    /// step does not carry (a folder for a folder action, a destination for a move, words for a create).
    /// </summary>
    private AssistantStep? Rewrite(AssistantStep step, AgentAction from, AgentAction to)
    {
        var info = ActionCatalog.Info(to);
        var fromInfo = ActionCatalog.Info(from);
        var args = step.Args.Where(a => info.Args.Contains(a.Name, StringComparer.Ordinal)).ToList();
        var thing = Thing(step);
        var container = Refs(step, "destination").FirstOrDefault() ?? Refs(step, "toList").FirstOrDefault()
            ?? (fromInfo.Family == "contact" ? Refs(step, "list").FirstOrDefault() : null)
            ?? (from == AgentAction.UpdateEvent ? Refs(step, "calendar").FirstOrDefault() : null)
            ?? Refs(step, "parent").FirstOrDefault();
        var newName = Refs(step, "newName").FirstOrDefault() ?? Refs(step, "newTitle").FirstOrDefault();
        // The person of a contact reading: the person slot, the thing itself, or a mail reading's sender; never an event's attendee or a draft's recipient.
        var person = Refs(step, "person").FirstOrDefault() ?? (thing is { Kind: AssistantRefKinds.Person or AssistantRefKinds.Group } ? thing : null)
            ?? (ActionCatalog.IsMailAction(from) ? Refs(step, "sender").FirstOrDefault() : null);
        var containerLike = thing is { Kind: AssistantRefKinds.Container or AssistantRefKinds.Unknown } || thing?.Props?.WellKnown is not null ? thing : null;
        string? on = null;
        bool Set(string name, string? refId)
        {
            if (refId is null || !info.Args.Contains(name, StringComparer.Ordinal)) return false;
            args.RemoveAll(a => a.Name == name);
            args.Add(new AssistantArg(name, refId));
            return true;
        }

        void SetLiteral(string name, string? value)
        {
            if (value is null || !info.Args.Contains(name, StringComparer.Ordinal)) return;
            args.RemoveAll(a => a.Name == name);
            args.Add(new AssistantArg(name, Value: value));
        }

        switch (info.Target)
        {
            case AssistantTargets.Folder:
                if (!Set("folder", containerLike?.Id ?? container?.Id ?? step.RefId("folder")) && _t.Request.Selection?.Kind != AssistantSelectionKind.Folder) return null;
                Set("newName", newName?.Id);
                break;
            case AssistantTargets.List:
                if (!Set("list", containerLike?.Id ?? container?.Id)) return null;
                args.RemoveAll(a => a.Name == "list" && a.Ref != (containerLike?.Id ?? container?.Id));
                Set("newName", newName?.Id);
                break;
            case AssistantTargets.Mails:
                on = thing is null ? null : AsItem(thing).Id;
                if (to == AgentAction.MoveToFolder && !Set("destination", container?.Id)) return null;
                if (fromInfo.Target is AssistantTargets.Folder or AssistantTargets.List) args.RemoveAll(a => a.Name is "folder" or "list");
                break;
            case AssistantTargets.Events:
                on = thing is null ? null : AsItem(thing).Id;
                if (on is null && step.On != AssistantStep.OnSelection && step.From is null && !step.Args.Any(a => a.Name is "when" or "attendee" or "calendar")) return null;
                Set("newTitle", newName?.Id);
                if (from == AgentAction.RespondToEvent) args.RemoveAll(a => a.Name is "response" or "message");
                if (to == AgentAction.RespondToEvent && from == AgentAction.DeleteEvent) SetLiteral("response", "Decline");
                if (ActionCatalog.Creates(from))
                {
                    Set("newStart", step.RefId("start"));
                    if (Refs(step, "start").FirstOrDefault()?.Time?.Assumed?.Contains("date") == true) SetLiteral("series", "true");
                }

                break;
            case AssistantTargets.Tasks:
                on = thing is null ? null : AsItem(thing).Id;
                if (on is null && step.On != AssistantStep.OnSelection && step.From is null && !step.Args.Any(a => a.Name is "due" or "list" or "overdue" or "important" or "myDay")) return null;
                if (to == AgentAction.UpdateTask)
                {
                    if (fromInfo.Target is AssistantTargets.Folder or AssistantTargets.List || from == AgentAction.MoveToFolder || from == AgentAction.UpdateEvent) args.RemoveAll(a => a.Name == "list");
                    if (from == AgentAction.MoveToFolder || from == AgentAction.AddContactToList || from == AgentAction.UpdateEvent) { if (!Set("toList", container?.Id)) return null; }
                    if (from is AgentAction.RenameFolder or AgentAction.RenameTaskList or AgentAction.UpdateEvent) Set("newTitle", newName?.Id);
                    if (ActionCatalog.Creates(from))
                    {
                        Set("newDue", step.RefId("due"));
                        Set("toList", step.RefId("list"));
                        args.RemoveAll(a => a.Name == "list");
                        SetLiteral("setMyDay", step.Literal("myDay") == "true" ? "true" : null);
                        SetLiteral("setImportant", step.Literal("important") == "true" ? "true" : null);
                    }
                }

                break;
            case AssistantTargets.Contacts:
                if (!Set("person", person?.Id)) return null;
                if (to is AgentAction.AddContactToList or AgentAction.RemoveContactFromList && !Set("list", container?.Id)) return null;
                break;
            case AssistantTargets.None:
                if (to == AgentAction.CreateTask && from == AgentAction.UpdateTask)
                {
                    if (thing is null || NamesOf(thing).Count == 0) return null;
                    Set("title", AsContent(thing).Id);
                    Set("due", step.RefId("newDue"));
                    Set("list", step.RefId("toList"));
                    SetLiteral("myDay", step.Literal("setMyDay") == "true" ? "true" : null);
                    SetLiteral("important", step.Literal("setImportant") == "true" ? "true" : null);
                }

                break;
        }

        return step with { Action = to.ToString(), On = step.On == AssistantStep.OnSelection ? step.On : on, Args = args, Alt = null };
    }

    /// <summary>A container, unknown or content ref as the item it names (the words become the item's topic).</summary>
    private AssistantRef AsItem(AssistantRef r)
    {
        if (r.Kind is AssistantRefKinds.Item or AssistantRefKinds.Person or AssistantRefKinds.Group or AssistantRefKinds.Org) return r;
        var id = r.Id + "~item";
        if (_synthetic.TryGetValue(id, out var existing)) return existing;
        var words = NamesOf(r).FirstOrDefault();
        var item = r with { Id = id, Kind = AssistantRefKinds.Item, By = r.By is AssistantRefBy.Name or AssistantRefBy.Description ? r.By : AssistantRefBy.Name, Kinds = null, Members = null, Content = null,
            Props = new AssistantRefProps(Topic: words, WellKnown: r.Props?.WellKnown) };
        _synthetic[id] = item;
        return item;
    }

    /// <summary>An item ref as dictated content (its words become a title).</summary>
    private AssistantRef AsContent(AssistantRef r)
    {
        var id = r.Id + "~content";
        if (_synthetic.TryGetValue(id, out var existing)) return existing;
        var words = NamesOf(r).First();
        var content = new AssistantRef(id, AssistantRefKinds.Content, AssistantRefBy.Dictated, r.Text, words, Content: new AssistantContent(AssistantRefBy.Dictated, words));
        _synthetic[id] = content;
        return content;
    }

    private static bool SameThing(AssistantStep a, AssistantStep b)
    {
        static string Key(AssistantStep s) => (s.On ?? "") + "|" + string.Join(",", s.Args.Where(x => x.Ref is not null || x.Refs is { Count: > 0 }).OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + "=" + (x.Ref ?? string.Join("+", x.Refs!))));
        return Key(a).Replace("~item", "") == Key(b).Replace("~item", "");
    }

    // ---- existence ------------------------------------------------------------------------------------------------

    private enum Existence
    {
        /// <summary>A slot holds a ref of a kind the action cannot take, a required slot is empty, or the device says the thing is of another kind.</summary>
        Impossible,
        /// <summary>The device has the named thing, but the user's role on it does not fit the action (deleting someone else's event, declining one's own).</summary>
        WrongRole,
        /// <summary>The device was asked for a named thing and has none.</summary>
        Missing,
        /// <summary>Nothing contradicts the reading (a selection, a link, a pronoun, a filter, a mail).</summary>
        Plausible,
    }

    /// <summary><see cref="Found"/>: the device confirmed a named thing or container of the kind the action needs.</summary>
    private readonly record struct Evidence(Existence Status, bool Found)
    {
        public static readonly Evidence Plausible = new(Existence.Plausible, false);
        public static readonly Evidence Confirmed = new(Existence.Plausible, true);
        public static readonly Evidence Impossible = new(Existence.Impossible, false);
        public static readonly Evidence Missing = new(Existence.Missing, false);
        public bool Stands => Status == Existence.Plausible;

        public Evidence And(Evidence other) => new((Existence)Math.Min((int)Status, (int)other.Status), Found || other.Found);
    }

    /// <summary>Whether the thing this candidate needs exists on the device with the kind the action needs.</summary>
    private async Task<Evidence> ExistsAsync(ActionCandidate c, CancellationToken ct)
    {
        var step = c.Step;
        var action = c.Action;
        var thing = Thing(step);
        switch (ActionCatalog.Target(action))
        {
            case AssistantTargets.Mails:
                if (thing is { Kind: AssistantRefKinds.Container or AssistantRefKinds.Account }) return Evidence.Impossible;
                if (thing is { Kind: AssistantRefKinds.Unknown, Kinds: { Count: > 0 } kinds } && !kinds.Contains(AssistantRefKinds.Item) && !kinds.Contains(AssistantRefKinds.Person) && !kinds.Contains(AssistantRefKinds.Org)) return Evidence.Impossible;
                return ContainerOk(step, "destination", ItemKind.Folder, required: action == AgentAction.MoveToFolder).And(ContainerOk(step, "category", ItemKind.Category)).And(ContainerOk(step, "folder", ItemKind.Folder));
            case AssistantTargets.Events:
            {
                var evidence = ContainerOk(step, "calendar", ItemKind.Calendar).And(await ItemExistsAsync(step, thing, ItemKind.Event, ct).ConfigureAwait(false));
                if (!evidence.Stands) return evidence;
                return await OwnershipAllowsAsync(step, action, thing, ct).ConfigureAwait(false) ? evidence : new Evidence(Existence.WrongRole, evidence.Found);
            }
            case AssistantTargets.Tasks:
                return ContainerOk(step, "list", ItemKind.TaskList).And(ContainerOk(step, "toList", ItemKind.TaskList, required: action == AgentAction.UpdateTask && step.Arg("toList") is not null))
                    .And(await ItemExistsAsync(step, thing, ItemKind.Task, ct).ConfigureAwait(false));
            case AssistantTargets.Contacts:
                return ContainerOk(step, "list", ItemKind.ContactList, required: action is AgentAction.AddContactToList or AgentAction.RemoveContactFromList).And(ContainerOk(step, "category", ItemKind.Category))
                    .And(await ContactExistsAsync(step, thing, ct).ConfigureAwait(false));
            case AssistantTargets.Folder:
                if (_t.Request.Selection?.Kind == AssistantSelectionKind.Folder && !Refs(step, "folder").Any()) return Evidence.Plausible;
                return ContainerOk(step, "folder", ItemKind.Folder, required: true);
            case AssistantTargets.List:
                return ContainerOk(step, "list", ItemKind.TaskList, required: true);
            default:
                return Evidence.Plausible;
        }
    }

    /// <summary>
    /// Every container ref of the argument names a container of the kind (score 0.7 or better; 0.95 when the reader typed it
    /// as another kind). A name the device has only as another kind of container is impossible; one it has not at all is
    /// missing; no ref is plausible unless required.
    /// </summary>
    private Evidence ContainerOk(AssistantStep step, string argName, ItemKind kind, bool required = false)
    {
        var refs = Refs(step, argName).ToList();
        if (refs.Count == 0) return required ? Evidence.Impossible : Evidence.Plausible;
        var result = Evidence.Plausible;
        foreach (var r in refs)
        {
            if (r.Kind == AssistantRefKinds.Account) { if (kind is ItemKind.Calendar or ItemKind.TaskList) continue; return Evidence.Impossible; }
            if (r.Kind is AssistantRefKinds.Person or AssistantRefKinds.Group or AssistantRefKinds.Org or AssistantRefKinds.Time or AssistantRefKinds.Content or AssistantRefKinds.Self) return Evidence.Impossible;
            if (_brain.Find(Brain.BrainEntryKind.ContainerSynonym).Any(e => e.Subject.ItemKind == kind.ToString() && (ConceptMatches(e.Value, r) || e.SaidAs.Any(s => TextMatch.Same(s, r.Text))))) { result = result.And(Evidence.Confirmed); continue; }
            if (r.Props?.WellKnown is { } wk && kind == ItemKind.Folder && WellKnownContainers(wk, null).Any()) { result = result.And(Evidence.Confirmed); continue; }
            var typed = KindOfType(r.Props?.Type);
            var untyped = r.Props is null ? r : r with { Props = r.Props with { Type = null } };
            if (ContainerCandidates(untyped, kind, null).Any(x => x.Score >= (typed is null || typed == kind ? 0.7 : 0.95))) { result = result.And(Evidence.Confirmed); continue; }
            // Only a container of another kind with exactly this name says the reader picked the wrong kind; a partial match says nothing.
            var otherKinds = new[] { ItemKind.Folder, ItemKind.Calendar, ItemKind.TaskList, ItemKind.Category, ItemKind.ContactList }.Where(k => k != kind);
            return otherKinds.Any(k => ContainerCandidates(untyped, k, null).Any(x => x.Score >= 0.95)) ? Evidence.Impossible : Evidence.Missing;
        }

        return result;
    }

    /// <summary>A named item of the kind exists; a selection, a link, a pronoun or a filter-only step is taken on trust (the target resolver handles those).</summary>
    private async Task<Evidence> ItemExistsAsync(AssistantStep step, AssistantRef? thing, ItemKind kind, CancellationToken ct)
    {
        if (step.On == AssistantStep.OnSelection) return SelectionKindMatches(kind) ? Evidence.Plausible : Evidence.Impossible;
        if (step.From is { } from) return !_resolved.TryGetValue(from.Step, out var source) || ActionCatalog.ItemKindOf(source.Action) == kind ? Evidence.Plausible : Evidence.Impossible;
        if (thing is null || thing.By == AssistantRefBy.Pronoun || thing.Kind is AssistantRefKinds.Self or AssistantRefKinds.Time) return Evidence.Plausible;
        // A person is not a task or an event (the contact family takes such a thing).
        if (thing.Kind is AssistantRefKinds.Person or AssistantRefKinds.Group or AssistantRefKinds.Org) return Evidence.Impossible;
        if (thing.Kind == AssistantRefKinds.Unknown && thing.Kinds is { Count: > 0 } kinds && !kinds.Contains(AssistantRefKinds.Item)) return Evidence.Impossible;
        var names = NamesOf(thing);
        if (names.Count == 0) return Evidence.Plausible;
        var key = kind + ":" + string.Join("|", names.Select(TextMatch.Key));
        if (!_exists.TryGetValue(key, out var found))
        {
            foreach (var name in names)
            {
                if (kind == ItemKind.Task) found = (await _q.FindTasksAsync(null, TaskQuery.Empty with { TitleWords = name, IncludeCompleted = true, Limit = 1 }, 1, ct).ConfigureAwait(false)).Count > 0;
                else found = await FirstEventAsync(thing, ct, name).ConfigureAwait(false) is not null;
                if (found) break;
            }

            _exists[key] = found;
        }

        return found ? Evidence.Confirmed : Evidence.Missing;
    }
    /// <summary>The first event the device finds under one of the thing's names (memoized per name).</summary>
    private async Task<AgentEventSummary?> FirstEventAsync(AssistantRef thing, CancellationToken ct, string? oneName = null)
    {
        foreach (var name in oneName is null ? NamesOf(thing) : [oneName])
        {
            var key = TextMatch.Key(name);
            if (!_eventByTitle.TryGetValue(key, out var summary))
            {
                var hits = await _q.FindEventsAsync(null, EventQuery.Empty with { TitleWords = name, Limit = 1 }, 1, ct).ConfigureAwait(false);
                _eventByTitle[key] = summary = hits.Count > 0 ? hits[0] : null;
            }

            if (summary is not null) return summary;
        }

        return null;
    }

    /// <summary>
    /// A decline answers someone else's invitation; one's own event is cancelled by deleting it. The device says who
    /// organizes; a reading that marks the item an invitation or names its organizer counts as the device's word too.
    /// </summary>
    private async Task<bool> OwnershipAllowsAsync(AssistantStep step, AgentAction action, AssistantRef? thing, CancellationToken ct)
    {
        var declines = action == AgentAction.RespondToEvent && string.Equals(step.Literal("response"), "Decline", StringComparison.OrdinalIgnoreCase);
        if (!declines && action != AgentAction.DeleteEvent) return true;
        if (thing is null || thing.By == AssistantRefBy.Pronoun || step.On == AssistantStep.OnSelection || step.From is not null) return true;
        var invitation = string.Equals(thing.Props?.ItemType, "invitation", StringComparison.OrdinalIgnoreCase)
            || step.Args.SelectMany(a => a.Refs ?? (a.Ref is null ? [] : [a.Ref])).Select(Ref).Any(r => r?.Role == AssistantRoles.Organizer)
            || R.Refs.Any(r => r.Role == AssistantRoles.Organizer && r.Kind == AssistantRefKinds.Person);
        var found = await FirstEventAsync(thing, ct).ConfigureAwait(false);
        if (found is null) return true;
        var own = found.IsOrganizer && !invitation;
        return declines ? !own : own;
    }

    private async Task<Evidence> ContactExistsAsync(AssistantStep step, AssistantRef? thing, CancellationToken ct)
    {
        var people = Refs(step, "person").ToList();
        if (people.Count == 0 && thing is { Kind: AssistantRefKinds.Person or AssistantRefKinds.Group or AssistantRefKinds.Unknown }) people.Add(thing);
        if (people.Count == 0) return thing is null ? Evidence.Plausible : Evidence.Impossible;
        var result = Evidence.Plausible;
        foreach (var r in people.SelectMany(Expand))
        {
            if (r.Kind is AssistantRefKinds.Self || r.By is AssistantRefBy.Pronoun or AssistantRefBy.Self or AssistantRefBy.Relationship) continue;
            if (r.Kind is AssistantRefKinds.Container or AssistantRefKinds.Account or AssistantRefKinds.Time or AssistantRefKinds.Content) return Evidence.Impossible;
            if (r.Kind == AssistantRefKinds.Unknown && r.Kinds is { Count: > 0 } kinds && !kinds.Contains(AssistantRefKinds.Person)) return Evidence.Impossible;
            var candidates = await NameCandidatesAsync(r, ct).ConfigureAwait(false);
            if (!candidates.Any(c => c.Exact || c.Score >= 0.8)) return Evidence.Missing;
            result = result.And(Evidence.Confirmed);
        }

        return result;
    }
}
