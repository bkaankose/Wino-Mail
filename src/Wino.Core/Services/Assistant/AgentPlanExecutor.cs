#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain;
using Wino.Core.Services;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Services.Assistant;

/// <summary>
/// Runs a <see cref="ResolvedPlan"/> on the device. The plan arrives fully resolved: every step names its account,
/// folders, calendars, lists, people and times by the device's own ids, so nothing here matches names. Steps run in
/// <see cref="ResolvedStep.N"/> order; a step whose target is <see cref="StepTarget.FromStep"/> chains from an earlier
/// step's outcome (its items, or the people of its items). Each step reports one <see cref="StepOutcome"/> through the
/// progress and in the returned <see cref="PlanOutcome"/>: light <see cref="ItemRef"/>s only, and one human line for the
/// bubble with its strong parts marked with double asterisks. The executor never re-decides risk: the runtime's policy
/// already did, and the plan was confirmed before it reaches here.
/// </summary>
public sealed partial class AgentPlanExecutor : IAgentPlanExecutor
{
    private const int PageSize = 200;
    private const int ListedItems = 5;
    private const int MaxRecipientMails = 20;
    private const string TraceSource = "assistant";

    private readonly IMailService mailService;
    private readonly IFolderService folderService;
    private readonly IWinoRequestDelegator requestDelegator;
    private readonly ICalendarService calendarService;
    private readonly ITaskService taskService;
    private readonly IContactService contactService;
    private readonly IMailCategoryService mailCategoryService;
    private readonly IMimeFileService mimeFileService;
    private readonly IPreferencesService preferencesService;
    private readonly IAccountService accountService;
    private readonly IServiceProvider services;

    public AgentPlanExecutor(
        IMailService mailService,
        IFolderService folderService,
        IWinoRequestDelegator requestDelegator,
        ICalendarService calendarService,
        ITaskService taskService,
        IContactService contactService,
        IMailCategoryService mailCategoryService,
        IMimeFileService mimeFileService,
        IPreferencesService preferencesService,
        IAccountService accountService,
        IServiceProvider services)
    {
        this.mailService = mailService;
        this.folderService = folderService;
        this.requestDelegator = requestDelegator;
        this.calendarService = calendarService;
        this.taskService = taskService;
        this.contactService = contactService;
        this.mailCategoryService = mailCategoryService;
        this.mimeFileService = mimeFileService;
        this.preferencesService = preferencesService;
        this.accountService = accountService;
        this.services = services;
    }

    /// <summary>
    /// The platform's windows. The platform creates its host at runtime and sets it here before the first plan runs;
    /// a plan that needs a window while it is unset fails that step.
    /// </summary>
    public IAgentPlanHost? Host { get; set; }

    public async Task<PlanOutcome> ExecuteAsync(ResolvedPlan plan, IProgress<StepOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var run = new PlanRun(plan);
        var steps = plan.Steps.OrderBy(static step => step.N).ToList();
        var cancelled = false;

        foreach (var step in steps)
        {
            StepOutcome outcome;
            if (cancelled)
                outcome = new StepOutcome(step.N, StepStatus.Skipped, [], null);
            else if (ct.IsCancellationRequested)
            {
                cancelled = true;
                outcome = new StepOutcome(step.N, StepStatus.Cancelled, [], null);
            }
            else if (DependencyBlocks(step, run) is { } blocked)
                outcome = blocked;
            else
            {
                try
                {
                    outcome = await RunStepAsync(step, run, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    outcome = new StepOutcome(step.N, StepStatus.Cancelled, [], null);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An assistant step failed: {Action}.", step.Action);
                    outcome = Failed(step, ex.Message);
                }
            }

            run.Outcomes[step.N] = outcome;
            progress?.Report(outcome);
        }

        return new PlanOutcome(plan.Id, steps.Select(step => run.Outcomes[step.N]).ToList());
    }

    /// <summary>A step chained to one that did not run or found nothing is skipped, with the reason of its kind.</summary>
    private static StepOutcome? DependencyBlocks(ResolvedStep step, PlanRun run)
    {
        var source = step.Target is StepTarget.FromStep from ? from.N : step.DependsOn;
        if (source is not int n)
            return null;

        if (!run.Outcomes.TryGetValue(n, out var outcome) || outcome.Status is StepStatus.Done)
            return null;

        var reason = step.Target is StepTarget.FromStep { Part: not "items" }
            ? Translator.VoiceIssue_NoResultPeople
            : ItemKindOf(step.Action) switch
            {
                ItemKind.Event => Translator.VoiceIssue_NoEvents,
                ItemKind.Task => Translator.VoiceIssue_ChainedTasksSkipped,
                ItemKind.Contact => Translator.VoiceIssue_ChainedContactsSkipped,
                _ => Translator.VoiceIssue_ChainedSkipped
            };

        return new StepOutcome(step.N, StepStatus.Skipped, [], string.Format(CultureInfo.CurrentCulture, reason, Gloss(step)));
    }

    private Task<StepOutcome> RunStepAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
        => step.Action switch
        {
            AgentAction.SearchMail => OpenSearchAsync(step, run, ct),
            AgentAction.MarkRead => RunMailOperationAsync(step, run, MailOperation.MarkAsRead, Translator.VoiceResult_MarkedRead, ct),
            AgentAction.MarkUnread => RunMailOperationAsync(step, run, MailOperation.MarkAsUnread, Translator.VoiceResult_MarkedUnread, ct),
            AgentAction.Flag => RunMailOperationAsync(step, run, MailOperation.SetFlag, Translator.VoiceResult_Flagged, ct),
            AgentAction.Unflag => RunMailOperationAsync(step, run, MailOperation.ClearFlag, Translator.VoiceResult_Unflagged, ct),
            AgentAction.Archive => RunMailOperationAsync(step, run, MailOperation.Archive, Translator.VoiceResult_Archived, ct),
            AgentAction.Unarchive => RunMailOperationAsync(step, run, MailOperation.UnArchive, Translator.VoiceResult_Unarchived, ct),
            AgentAction.Delete => RunMailOperationAsync(step, run, MailOperation.SoftDelete, Translator.VoiceResult_Deleted, ct),
            AgentAction.MoveToFolder => MoveToFolderAsync(step, run, ct),
            AgentAction.MarkJunk => RunMailOperationAsync(step, run, MailOperation.MoveToJunk, Translator.VoiceResult_MarkedJunk, ct),
            AgentAction.MarkNotJunk => RunMailOperationAsync(step, run, MailOperation.MarkAsNotJunk, Translator.VoiceResult_MarkedNotJunk, ct),
            AgentAction.MoveToFocused => MoveToInboxTabAsync(step, run, focused: true, ct),
            AgentAction.MoveToOther => MoveToInboxTabAsync(step, run, focused: false, ct),
            AgentAction.Pin => PinMailsAsync(step, run, pin: true, ct),
            AgentAction.Unpin => PinMailsAsync(step, run, pin: false, ct),
            AgentAction.Categorize => CategorizeMailsAsync(step, run, assign: true, ct),
            AgentAction.Uncategorize => CategorizeMailsAsync(step, run, assign: false, ct),
            AgentAction.Summarize => SummarizeAsync(step, run, ct),
            AgentAction.Translate => TranslateAsync(step, run, ct),
            AgentAction.AnalyzeMail => AnalyzeMailsAsync(step, run, ct),
            AgentAction.CopyCode => CopyCodeAsync(step, run, ct),
            AgentAction.Unsubscribe => UnsubscribeAsync(step, run, ct),
            AgentAction.SendDrafts => SendDraftsAsync(step, run, ct),
            AgentAction.CreateDraft => CreateDraftAsync(step, run, ct),
            AgentAction.CreateReplyDraft => CreateReplyDraftAsync(step, run, ct),
            AgentAction.CreateEvent => CreateEventAsync(step, run, ct),
            AgentAction.FindEvents or AgentAction.DeleteEvent or AgentAction.UpdateEvent or AgentAction.SetEventShowAs
                or AgentAction.RespondToEvent or AgentAction.SetEventReminder or AgentAction.JoinMeeting => RunEventStepAsync(step, run, ct),
            AgentAction.CreateTask => CreateTaskAsync(step, run, ct),
            AgentAction.FindTasks or AgentAction.CompleteTask or AgentAction.ReopenTask or AgentAction.UpdateTask
                or AgentAction.AddTaskStep or AgentAction.DeleteTask => RunTaskStepAsync(step, run, ct),
            AgentAction.CreateTaskList or AgentAction.RenameTaskList or AgentAction.DeleteTaskList => RunTaskListStepAsync(step, run, ct),
            AgentAction.CreateContact => CreateContactAsync(step, run, ct),
            AgentAction.FindContacts or AgentAction.FavoriteContact or AgentAction.UnfavoriteContact or AgentAction.AddContactToList
                or AgentAction.RemoveContactFromList or AgentAction.CategorizeContact or AgentAction.DeleteContact => RunContactStepAsync(step, run, ct),
            AgentAction.MarkFolderRead or AgentAction.EmptyFolder or AgentAction.PinFolder or AgentAction.UnpinFolder or AgentAction.RenameFolder
                or AgentAction.CreateFolder or AgentAction.DeleteFolder or AgentAction.SyncFolder => RunFolderStepAsync(step, run, ct),
            AgentAction.Undo => UndoAsync(step, run, ct),
            AgentAction.SyncMail => SyncMailAsync(step, run, ct),
            AgentAction.CreateMailRule => CreateMailRuleAsync(step, ct),
            AgentAction.ChangeSetting => ChangeSettingAsync(step),
            AgentAction.OpenView => OpenViewAsync(step),
            _ => Task.FromResult(Failed(step, Translator.VoiceIssue_NotUnderstood))
        };

    /// <summary>The kind of item an action acts on, for chaining and for the wording of a skipped or empty step.</summary>
    private static ItemKind? ItemKindOf(AgentAction action) => action switch
    {
        AgentAction.FindEvents or AgentAction.DeleteEvent or AgentAction.UpdateEvent or AgentAction.SetEventShowAs
            or AgentAction.RespondToEvent or AgentAction.SetEventReminder or AgentAction.JoinMeeting => ItemKind.Event,
        AgentAction.FindTasks or AgentAction.CompleteTask or AgentAction.ReopenTask or AgentAction.UpdateTask
            or AgentAction.AddTaskStep or AgentAction.DeleteTask => ItemKind.Task,
        AgentAction.FindContacts or AgentAction.FavoriteContact or AgentAction.UnfavoriteContact or AgentAction.AddContactToList
            or AgentAction.RemoveContactFromList or AgentAction.CategorizeContact or AgentAction.DeleteContact => ItemKind.Contact,
        AgentAction.MarkFolderRead or AgentAction.EmptyFolder or AgentAction.PinFolder or AgentAction.UnpinFolder or AgentAction.RenameFolder
            or AgentAction.DeleteFolder or AgentAction.SyncFolder => ItemKind.Folder,
        AgentAction.RenameTaskList or AgentAction.DeleteTaskList => ItemKind.TaskList,
        AgentAction.CreateEvent or AgentAction.CreateTask or AgentAction.CreateTaskList or AgentAction.CreateContact or AgentAction.CreateFolder
            or AgentAction.CreateDraft or AgentAction.SyncMail => null,
        _ => ItemKind.Mail
    };

    // ---- chaining ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The addresses a chained step takes from an earlier step's result: the senders or recipients of its mails and
    /// drafts, the attendees or organizer of its events, the contacts it found. Never the user's own addresses;
    /// recipients come from the stored MIME and are skipped for mails that are not downloaded.
    /// </summary>
    private async Task<List<Recipient>> PeopleOfStepAsync(PlanRun run, int sourceStep, string part, CancellationToken ct)
    {
        var own = await OwnAddressesAsync(run).ConfigureAwait(false);
        var people = new List<Recipient>();

        if (run.Contacts.TryGetValue(sourceStep, out var contacts))
        {
            people.AddRange(contacts
                .Where(static contact => !string.IsNullOrWhiteSpace(contact.PrimaryEmailAddress))
                .Select(static contact => new Recipient(contact.PrimaryEmailAddress, contact.DisplayName)));
        }
        else if (run.Events.TryGetValue(sourceStep, out var events))
        {
            foreach (var target in events)
            {
                if (part != "organizer")
                {
                    var attendees = await GetAttendeesAsync(target.Event, run).ConfigureAwait(false);
                    people.AddRange(attendees.Select(static attendee => new Recipient(attendee.Email ?? string.Empty, attendee.Name)));
                }

                if (part is "people" or "organizer" && !string.IsNullOrWhiteSpace(target.Event.OrganizerEmail))
                    people.Add(new Recipient(target.Event.OrganizerEmail, target.Event.OrganizerDisplayName));
            }
        }
        else if (run.Mails.TryGetValue(sourceStep, out var mails))
        {
            var wantsSenders = part is "sender" or "people";
            var wantsRecipients = part is "recipients" or "people" || mails.All(static mail => mail.IsDraft);
            if (wantsSenders)
                people.AddRange(mails.Where(static mail => !mail.IsDraft).Select(static mail => new Recipient(mail.FromAddress ?? string.Empty, mail.FromName)));

            if (wantsRecipients)
            {
                foreach (var mail in mails.Take(MaxRecipientMails))
                {
                    if (await ReadMimeAsync(mail, ct).ConfigureAwait(false) is not { } message)
                        continue;

                    people.AddRange(message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(static mailbox => new Recipient(mailbox.Address, mailbox.Name)));
                }
            }
        }

        return people
            .Where(person => !string.IsNullOrWhiteSpace(person.Address) && person.Address.Contains('@') && !own.Contains(person.Address.Trim()))
            .Select(static person => person with { Address = person.Address.Trim(), Name = string.IsNullOrWhiteSpace(person.Name) ? null : person.Name.Trim() })
            .DistinctBy(static person => person.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<HashSet<string>> OwnAddressesAsync(PlanRun run)
    {
        if (run.OwnAddresses is { } cached)
            return cached;

        var accounts = await AccountsAsync(run).ConfigureAwait(false);
        return run.OwnAddresses = new HashSet<string>(
            accounts.SelectMany(static account => new[] { account.Address, account.AuthenticationAddress })
                .Where(static address => !string.IsNullOrWhiteSpace(address))
                .Select(static address => address.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<MailAccount>> AccountsAsync(PlanRun run)
        => run.Accounts ??= await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];

    private async Task<MailAccount?> AccountAsync(PlanRun run, Guid? accountId)
        => accountId is Guid id ? (await AccountsAsync(run).ConfigureAwait(false)).FirstOrDefault(account => account.Id == id) : null;

    /// <summary>The accounts a system step names: the one in its arguments, the ones of its items, else every account.</summary>
    private async Task<List<MailAccount>> StepAccountsAsync(ResolvedStep step, PlanRun run)
    {
        var accounts = await AccountsAsync(run).ConfigureAwait(false);
        if (step.Args is StepArgs.SystemArgs { AccountId: Guid named })
            return accounts.Where(account => account.Id == named).ToList();

        var ids = ItemRefsOf(step.Target).Select(static item => item.AccountId).OfType<Guid>().ToHashSet();
        return ids.Count > 0 ? accounts.Where(account => ids.Contains(account.Id)).ToList() : accounts.ToList();
    }

    private static IReadOnlyList<ItemRef> ItemRefsOf(StepTarget target) => target switch
    {
        StepTarget.MailItems mails => mails.Items,
        StepTarget.EventItems events => events.Items,
        StepTarget.TaskItems tasks => tasks.Items,
        StepTarget.ContactItems contacts => contacts.Items,
        _ => []
    };

    /// <summary>The item refs of an earlier step's outcome that are of <paramref name="kind"/> (drafts count as mails).</summary>
    private static List<ItemRef> TouchedOf(PlanRun run, int sourceStep, ItemKind kind)
        => run.Outcomes.TryGetValue(sourceStep, out var outcome)
            ? outcome.Touched.Where(item => item.Kind == kind || (kind == ItemKind.Mail && item.Kind == ItemKind.Draft)).ToList()
            : [];

    // ---- MIME ----------------------------------------------------------------------------------------------------

    private async Task<MimeMessage?> ReadMimeAsync(MailCopy mail, CancellationToken ct)
        => (await ReadMimeInformationAsync(mail, ct).ConfigureAwait(false))?.MimeMessage;

    /// <summary>The stored MIME of a mail, or null when it is not downloaded or cannot be read.</summary>
    private async Task<MimeMessageInformation?> ReadMimeInformationAsync(MailCopy mail, CancellationToken ct)
    {
        if (mail.AssignedAccount is not { } account)
            return null;

        if (await TryReadMimeAsync(mail, account.Id, ct).ConfigureAwait(false) is { MimeMessage: not null } cached)
            return cached;

        // IMAP and the other servers keep only headers until a mail is opened: fetch the body the way the reader does,
        // then read it again. A mail that cannot be downloaded (offline, deleted on the server) stays null.
        try
        {
            await SynchronizationManager.Instance.DownloadMimeMessageAsync(mail, account.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Could not download a mail's MIME for an assistant step.");
            return null;
        }

        return await TryReadMimeAsync(mail, account.Id, ct).ConfigureAwait(false);
    }

    private async Task<MimeMessageInformation?> TryReadMimeAsync(MailCopy mail, Guid accountId, CancellationToken ct)
    {
        try
        {
            return await mimeFileService.GetMimeMessageInformationAsync(mail.FileId, accountId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Could not read a mail's MIME for an assistant step.");
            return null;
        }
    }

    // ---- outcomes and wording --------------------------------------------------------------------------------------

    private static StepOutcome Done(ResolvedStep step, IEnumerable<ItemRef> touched, string detail)
        => new(step.N, StepStatus.Done, touched.ToList(), detail);

    /// <summary>A failure: the reason in the user's words, inside "Couldn't do X" when the step has a gloss.</summary>
    private static StepOutcome Failed(ResolvedStep step, string reason)
        => new(step.N, StepStatus.Failed, [], string.IsNullOrWhiteSpace(step.Gloss)
            ? reason
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Failed, Gloss(step), reason));

    private static StepOutcome NothingMatched(ResolvedStep step, string template)
        => new(step.N, StepStatus.NothingMatched, [], string.Format(CultureInfo.CurrentCulture, template, Gloss(step)));

    private static string Gloss(ResolvedStep step)
        => string.IsNullOrWhiteSpace(step.Gloss) ? step.Action.ToString() : step.Gloss.Replace("**", string.Empty).Trim();

    private static string CountText(int count)
        => count == 1 ? Translator.VoiceCount_Mail : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Mails, count);

    private static string DraftsText(int count)
        => count == 1 ? Translator.VoiceCount_Draft : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Drafts, count);

    private static string EventsText(int count)
        => count == 1 ? Translator.VoiceCount_Event : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Events, count);

    private static string TasksText(int count)
        => count == 1 ? Translator.VoiceCount_Task : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Tasks, count);

    private static string ContactsText(int count)
        => count == 1 ? Translator.VoiceCount_Contact : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Contacts, count);

    private static string AccountsLabel(IReadOnlyList<MailAccount> accounts)
        => accounts.Count == 1
            ? AccountTitle(accounts[0])
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_AccountCount, accounts.Count);

    private static string AccountTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : account.Address ?? string.Empty;

    private static string Subject(MailCopy mail)
        => string.IsNullOrWhiteSpace(mail.Subject) ? Translator.VoiceResult_NoSubject : Plain(mail.Subject);

    private static string SenderName(MailCopy mail)
        => string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress ?? string.Empty : mail.FromName;

    /// <summary>Text shown inside a marked line: the user's own marks must not split ours.</summary>
    private static string Plain(string text) => text.Replace("**", string.Empty).Trim();

    private static string Clean(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();

    private static string MinutesText(int minutes)
        => minutes % 1440 == 0 ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Days, minutes / 1440)
            : minutes % 60 == 0 ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Hours, minutes / 60)
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Minutes, minutes);

    private static string LanguageName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code.Trim());
            return string.IsNullOrWhiteSpace(culture.DisplayName) ? code.Trim() : culture.DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return code.Trim();
        }
    }

    private static bool SameText(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
           && string.Compare(left.Trim(), right.Trim(), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    private static bool SameAddress(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> contains <paramref name="part"/>, ignoring case and accents ("Ayşe" and "ayse").</summary>
    private static bool Fits(string? text, string? part)
        => !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(part)
           && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, part.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>Whether every word of <paramref name="words"/> appears in one of <paramref name="texts"/>.</summary>
    private static bool FitsWords(string? words, params string?[] texts)
    {
        if (string.IsNullOrWhiteSpace(words))
            return true;

        var parts = words.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && parts.All(word => texts.Any(text => Fits(text, word)));
    }

    private static DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    // ---- state ---------------------------------------------------------------------------------------------------

    /// <summary>What the steps run so far acted on, by step number, so later steps can chain to them.</summary>
    private sealed class PlanRun(ResolvedPlan plan)
    {
        public ResolvedPlan Plan { get; } = plan;
        public Dictionary<int, StepOutcome> Outcomes { get; } = [];
        public Dictionary<int, List<MailCopy>> Mails { get; } = [];
        public Dictionary<int, List<EventTarget>> Events { get; } = [];
        public Dictionary<int, List<TaskTarget>> Tasks { get; } = [];
        public Dictionary<int, List<AccountContact>> Contacts { get; } = [];
        public Dictionary<Guid, List<CalendarEventAttendee>> Attendees { get; } = [];
        public List<MailAccount>? Accounts { get; set; }
        public HashSet<string>? OwnAddresses { get; set; }
    }

    /// <param name="IsOrganizer">The user organizes the event (or it has no organizer), so there is no invitation to answer.</param>
    /// <param name="AttendeeCount">Attendees other than the user.</param>
    private sealed record EventTarget(CalendarItem Event, MailAccount Account, bool IsOrganizer, int AttendeeCount);

    private sealed record TaskTarget(AccountTask Task, AccountTaskList? List, MailAccount Account);
}
