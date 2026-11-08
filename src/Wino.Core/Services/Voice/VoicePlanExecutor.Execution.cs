#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Domain.Models.Voice;
using Wino.Core.Requests.Tasks;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Services.Voice;

public sealed partial class VoicePlanExecutor
{
    private const int LargeDeleteCount = 20;
    private const int EmptyFolderCount = 50;
    private const int LargeArchiveCount = 100;
    private const double ConfidentEventThreshold = 0.8;
    private const string TraceSource = "voice-control";

    public IReadOnlyList<VoicePlanRisk> GetRisks(VoicePlanResolution resolution, IReadOnlyCollection<VoicePlanPreviewGroup> acceptedGroups)
    {
        var risks = new List<VoicePlanRisk>();

        foreach (var group in acceptedGroups)
        {
            foreach (var step in group.Steps)
            {
                var command = step.Command;
                if (VoiceCommand.ActsOnEvents(command.Action))
                {
                    AddEventRisks(resolution, group, step, risks);
                    continue;
                }

                if (command.Action is not (VoiceAction.Delete or VoiceAction.Archive))
                    continue;

                if (!resolution.MailTargets.TryGetValue(step.CommandIndex, out var targets) || targets.Count == 0)
                    continue;

                var count = targets.Sum(static target => target.Count);
                var mails = CountText(count);
                var accounts = AccountsLabel(targets.Select(static target => target.Account).DistinctBy(static account => account.Id).ToList());
                var folder = FolderLabel(targets);
                var selection = SourceSelection(step, resolution, acceptedGroups);
                var deleteButton = string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_DeleteButton, mails);
                var archiveButton = string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_ArchiveButton, mails);

                if (command.Action == VoiceAction.Archive)
                {
                    if (count >= LargeArchiveCount)
                        risks.Add(new VoicePlanRisk(step.CommandIndex, string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_Archive, mails, folder, accounts), false, archiveButton));

                    continue;
                }

                if (targets.Any(static target => target.Query.Folders.Any(static folder => folder.SpecialFolderType == SpecialFolderType.Deleted)))
                {
                    risks.Add(new VoicePlanRisk(step.CommandIndex, string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_PermanentDelete, mails, accounts), true, deleteButton));
                    continue;
                }

                var isBroad = group.Kind == VoicePlanAccountKind.AllAccounts || selection is null || IsBroad(selection);
                if (selection is not null && count >= EmptyFolderCount && IsFolderOnly(selection))
                    risks.Add(new VoicePlanRisk(step.CommandIndex, string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_EmptyFolder, mails, folder, accounts), false, deleteButton));
                else if (count >= LargeDeleteCount || isBroad)
                    risks.Add(new VoicePlanRisk(step.CommandIndex, string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_Delete, mails, folder, accounts), false, deleteButton));
            }
        }

        return risks;
    }

    public async Task<VoicePlanExecutionResult> ExecuteAsync(
        VoicePlanResolution resolution,
        IReadOnlyCollection<VoicePlanPreviewGroup> acceptedGroups,
        double planConfidence,
        IVoicePlanHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(acceptedGroups);
        ArgumentNullException.ThrowIfNull(host);

        var completed = new List<string>();
        var failed = new List<string>();
        string? summary = null;
        var mailsByCommand = new Dictionary<int, List<MailCopy>>();
        var namesAccount = acceptedGroups.Count(static group => group.Steps.Count > 0) > 1;

        var steps = acceptedGroups
            .SelectMany(static group => group.Steps.Select(step => (Group: group, Step: step)))
            .OrderBy(static entry => entry.Step.CommandIndex)
            .ToList();

        foreach (var (group, step) in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var context = new StepContext(group, step, resolution, mailsByCommand, planConfidence, host);
            try
            {
                var outcome = await ExecuteStepAsync(context, cancellationToken).ConfigureAwait(false);
                if (outcome.Summary is { } text)
                    summary = text;

                if (outcome.Error is { } error)
                    failed.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Failed, PlainText(step), error));
                else if (outcome.Line is { } line)
                    completed.Add(namesAccount && group.Account is { } account
                        ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_InAccount, line, AccountTitle(account))
                        : line);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "A voice step failed: {Action}.", step.Command.Action);
                failed.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Failed, PlainText(step), ex.Message));
            }
        }

        return new VoicePlanExecutionResult(completed, failed, summary);
    }

    private Task<StepResult> ExecuteStepAsync(StepContext context, CancellationToken cancellationToken)
        => context.Step.Command.Action switch
        {
            VoiceAction.MarkRead => RunMailOperationAsync(context, MailOperation.MarkAsRead, Translator.VoiceResult_MarkedRead, cancellationToken),
            VoiceAction.MarkUnread => RunMailOperationAsync(context, MailOperation.MarkAsUnread, Translator.VoiceResult_MarkedUnread, cancellationToken),
            VoiceAction.Flag => RunMailOperationAsync(context, MailOperation.SetFlag, Translator.VoiceResult_Flagged, cancellationToken),
            VoiceAction.Unflag => RunMailOperationAsync(context, MailOperation.ClearFlag, Translator.VoiceResult_Unflagged, cancellationToken),
            VoiceAction.Archive => RunMailOperationAsync(context, MailOperation.Archive, Translator.VoiceResult_Archived, cancellationToken),
            VoiceAction.Delete => RunMailOperationAsync(context, MailOperation.SoftDelete, Translator.VoiceResult_Deleted, cancellationToken),
            VoiceAction.Unarchive => RunMailOperationAsync(context, MailOperation.UnArchive, Translator.VoiceResult_Unarchived, cancellationToken),
            VoiceAction.FindEvents or VoiceAction.DeleteEvent or VoiceAction.UpdateEvent or VoiceAction.SetEventShowAs or VoiceAction.RespondToEvent => RunEventStepAsync(context),
            VoiceAction.SearchMail => OpenSearchAsync(context, cancellationToken),
            VoiceAction.Summarize => SummarizeAsync(context, cancellationToken),
            VoiceAction.CreateReplyDraft => OpenReplyDraftAsync(context, cancellationToken),
            VoiceAction.CreateDraft => OpenDraftAsync(context),
            VoiceAction.CreateTask => CreateTaskAsync(context),
            VoiceAction.CreateEvent => CreateEventAsync(context),
            _ => Task.FromResult(StepResult.None)
        };

    private async Task<StepResult> RunMailOperationAsync(StepContext context, MailOperation operation, string template, CancellationToken cancellationToken)
    {
        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        // The plan was confirmed in the bubble (twice for a risky delete), so the hard delete dialog is not asked again.
        var result = await requestDelegator.ExecuteWithResultAsync(new MailOperationPreperationRequest(
            operation,
            mails,
            toggleExecution: false,
            ignoreHardDeleteProtection: true)).ConfigureAwait(false);

        return result.Error is { } error
            ? StepResult.Failed(error.Message)
            : StepResult.Done(string.Format(CultureInfo.CurrentCulture, template, CountText(mails.Count)));
    }

    private async Task<StepResult> OpenSearchAsync(StepContext context, CancellationToken cancellationToken)
    {
        if (!TryGetTargets(context, out var targets))
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 1)
        {
            await context.Host.OpenMailAsync(mails[0]).ConfigureAwait(false);
            return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_OpenedMail, Subject(mails[0])));
        }

        var folders = targets.SelectMany(static target => target.Query.Folders).DistinctBy(static folder => folder.Id).ToList();
        var query = targets[0].Query;
        var selection = SourceSelection(context.Step, context.Resolution, null);
        var criteria = new MailSearchCriteria(
            query.SearchQuery ?? string.Empty,
            SearchMode.Local,
            selection is null || IsBroad(selection) ? MailSearchScope.AllFolders : selection.Scope == FolderScope.Subfolders ? MailSearchScope.Subfolders : MailSearchScope.CurrentFolder,
            MailSearchReach.DownloadedOnly,
            query.Sender ?? string.Empty,
            query.Subject ?? string.Empty,
            query.ReceivedAfterUtc,
            query.ReceivedBeforeUtc,
            query.RequireAttachments,
            query.ReadStatus,
            query.RequireFlagged,
            folders.Select(static folder => folder.Id).ToList(),
            targets.Select(static target => target.Account.Id).Distinct().ToList());

        await context.Host.OpenSearchAsync(targets[0].Account, folders, criteria).ConfigureAwait(false);
        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Showing, CountText(mails.Count)));
    }

    private async Task<StepResult> SummarizeAsync(StepContext context, CancellationToken cancellationToken)
    {
        if (!TryGetTargets(context, out _))
            return StepResult.Failed(Translator.VoiceResult_SummarizeOpenMail);

        var mail = (await GetMailsAsync(context, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (mail?.AssignedAccount is not { } account)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        if (services.GetService<IWinoIntelligenceCoordinator>() is not { } coordinator)
            return StepResult.Failed(Translator.VoiceResult_SummaryUnavailable);

        var mime = await mimeFileService.GetMimeMessageInformationAsync(mail.FileId, account.Id, cancellationToken).ConfigureAwait(false);
        var html = mime?.MimeMessage?.HtmlBody;
        if (string.IsNullOrWhiteSpace(html) && mime?.MimeMessage?.TextBody is { } text)
            html = WebUtility.HtmlEncode(text);
        if (string.IsNullOrWhiteSpace(html))
            return StepResult.Failed(Translator.VoiceResult_SummaryUnavailable);

        var contentKey = $"{account.Id:N}:{mail.UniqueId:N}";
        var intelligenceContext = new WinoIntelligenceContext(
            contentKey,
            account.Id,
            mail.UniqueId,
            mail.FileId,
            mail.Id,
            account.Address,
            account.ProviderType,
            account.Preferences?.IsSemanticIndexingEnabled == true,
            mail.Subject ?? string.Empty,
            string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress ?? string.Empty : mail.FromName,
            new DateTimeOffset(DateTime.SpecifyKind(mail.CreationDate, DateTimeKind.Utc)),
            html,
            IntelligenceMetadata: mail.IntelligenceMetadata);

        var result = await coordinator.SummarizeAsync(intelligenceContext, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Value))
            return StepResult.Failed(result.Error ?? Translator.VoiceResult_SummaryUnavailable);

        return new StepResult(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Summarized, Subject(mail)), null, result.Value.Trim());
    }

    private async Task<StepResult> OpenReplyDraftAsync(StepContext context, CancellationToken cancellationToken)
    {
        var mail = (await GetMailsAsync(context, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (mail?.AssignedAccount is not { } account)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        var mime = await mimeFileService.GetMimeMessageInformationAsync(mail.FileId, account.Id, cancellationToken).ConfigureAwait(false);
        if (mime?.MimeMessage is null)
            return StepResult.Failed(Translator.VoiceResult_MailNotDownloaded);

        var options = new DraftCreationOptions
        {
            Reason = DraftCreationReason.Reply,
            InitialBodyText = context.Step.Command.Draft?.Body ?? string.Empty,
            ReferencedMessage = new ReferencedMessage { MimeMessage = mime.MimeMessage, MailCopy = mail }
        };

        await CreateAndOpenDraftAsync(context.Host, account, options, mail).ConfigureAwait(false);
        var recipient = string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : mail.FromName;
        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ReplyOpened, recipient));
    }

    /// <summary>
    /// Creates the draft and queues it the way the composer's own new-mail and reply commands do, then lets the platform
    /// open it. <paramref name="referenceMail"/> is the mail a reply answers.
    /// </summary>
    private async Task CreateAndOpenDraftAsync(IVoicePlanHost host, MailAccount account, DraftCreationOptions options, MailCopy? referenceMail)
    {
        if (await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft).ConfigureAwait(false) is null)
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSpecialFolder, account.Name, Translator.VoiceStep_Folder_Drafts));

        var (draftMailCopy, draftBase64MimeMessage) = await mailService.CreateDraftAsync(account.Id, options).ConfigureAwait(false);
        var preparation = referenceMail is null
            ? new DraftPreparationRequest(account, draftMailCopy, draftBase64MimeMessage, options.Reason)
            : new DraftPreparationRequest(account, draftMailCopy, draftBase64MimeMessage, options.Reason, referenceMail);
        await requestDelegator.ExecuteAsync(preparation).ConfigureAwait(false);

        await host.OpenDraftAsync(account, draftMailCopy).ConfigureAwait(false);
    }

    private async Task<StepResult> OpenDraftAsync(StepContext context)
    {
        if (ResolveAccount(context.Group) is not { } account)
            return StepResult.Failed(Translator.VoiceIssue_AccountUnnamed);

        var draft = context.Step.Command.Draft;
        var people = context.Step.Command.People;
        var to = await ResolveAddressesAsync(account, draft?.To, people).ConfigureAwait(false);
        var cc = await ResolveAddressesAsync(account, draft?.Cc, people).ConfigureAwait(false);

        // MailToUri reads RFC 6068 text; the body is inserted into the draft's HTML as is, so it is encoded first.
        var parameters = new List<string>();
        if (cc.Count > 0)
            parameters.Add("cc=" + Uri.EscapeDataString(string.Join(",", cc)));
        if (!string.IsNullOrWhiteSpace(draft?.Subject))
            parameters.Add("subject=" + Uri.EscapeDataString(draft.Subject.Trim()));
        if (!string.IsNullOrWhiteSpace(draft?.Body))
            parameters.Add("body=" + Uri.EscapeDataString(WebUtility.HtmlEncode(draft.Body.Trim())));

        var mailTo = "mailto:" + string.Join(",", to.Select(Uri.EscapeDataString)) + (parameters.Count > 0 ? "?" + string.Join("&", parameters) : string.Empty);
        var options = new DraftCreationOptions
        {
            Reason = DraftCreationReason.Empty,
            MailToUri = new MailToUri(mailTo)
        };

        await CreateAndOpenDraftAsync(context.Host, account, options, null).ConfigureAwait(false);
        return StepResult.Done(to.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_DraftOpened, string.Join(", ", to))
            : Translator.VoiceResult_EmptyDraftOpened);
    }

    private async Task<StepResult> CreateTaskAsync(StepContext context)
    {
        var details = context.Step.Command.Task;
        if (details is null || string.IsNullOrWhiteSpace(details.Title))
            return StepResult.Failed(Translator.VoiceIssue_Missing_Title);

        var account = ResolveAccount(context.Group);
        var lists = await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
        var writable = lists.Where(static list => !list.IsReadOnly).ToList();
        var inAccount = account is null ? writable : writable.Where(list => list.MailAccountId == account.Id).ToList();
        var destination = (string.IsNullOrWhiteSpace(details.TaskListId) ? null : inAccount.FirstOrDefault(list => string.Equals(list.RemoteId, details.TaskListId, StringComparison.Ordinal)))
            ?? inAccount.FirstOrDefault(list => list.Id == preferencesService.LastUsedTaskListId)
            ?? inAccount.FirstOrDefault(static list => list.IsDefault)
            ?? inAccount.FirstOrDefault();

        if (destination is null)
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoTaskList, account is null ? string.Empty : AccountTitle(account)).Trim());

        var task = new AccountTask
        {
            Id = Guid.NewGuid(),
            MailAccountId = destination.MailAccountId,
            TaskListId = destination.Id,
            SourceKind = destination.SourceKind,
            Title = details.Title.Trim(),
            Notes = details.Notes ?? string.Empty,
            DueDate = details.DueDate?.ToDateTime(TimeOnly.MinValue),
            IsImportant = details.IsImportant
        };

        var request = new TaskActionRequest(destination.MailAccountId, TaskSynchronizerOperation.CreateTask, Task: task)
        {
            Trace = new RequestTrace(TraceSource, Guid.NewGuid())
        };

        await requestDelegator.ExecuteAsync(destination.MailAccountId, (IRequestBase[])[request]).ConfigureAwait(false);
        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_TaskCreated, task.Title));
    }

    private async Task<StepResult> CreateEventAsync(StepContext context)
    {
        var details = context.Step.Command.Event;
        if (details is null || string.IsNullOrWhiteSpace(details.Title))
            return StepResult.Failed(Translator.VoiceIssue_Missing_Title);

        if (ResolveAccount(context.Group) is not { } account)
            return StepResult.Failed(Translator.VoiceIssue_AccountUnnamed);

        var calendars = await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
        var calendar = (string.IsNullOrWhiteSpace(details.CalendarId) ? null : calendars.FirstOrDefault(calendar => !calendar.IsReadOnly && string.Equals(calendar.RemoteCalendarId, details.CalendarId, StringComparison.Ordinal)))
            ?? calendars.FirstOrDefault(static calendar => calendar.IsPrimary && !calendar.IsReadOnly)
            ?? calendars.FirstOrDefault(static calendar => !calendar.IsReadOnly);
        if (calendar is null)
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoCalendar, AccountTitle(account)));

        var start = ToLocalTime(details.StartLocal, details.TimeZoneId);
        var end = ToLocalTime(details.EndLocal, details.TimeZoneId);
        if (details.IsAllDay)
        {
            start = start.Date;
            end = end.Date <= start ? start.AddDays(1) : end.Date;
        }
        else if (end <= start)
        {
            end = start.AddMinutes(30);
        }

        // "Find a free hour tomorrow afternoon": the device picks the time from every calendar it has.
        if (details.Slot is { } slot)
        {
            var length = TimeSpan.FromMinutes(slot.DurationMinutes > 0 ? slot.DurationMinutes : 60);
            if (await FindFreeSlotAsync(slot, length, null).ConfigureAwait(false) is not { } free)
                return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoFreeSlot, details.Title.Trim()));

            start = free;
            end = free + length;
        }

        var attendees = new List<CalendarEventAttendeeDraft>();
        foreach (var name in details.Attendees ?? [])
        {
            var address = (await ResolveAddressesAsync(account, [name], context.Step.Command.People).ConfigureAwait(false)).FirstOrDefault();
            attendees.Add(new CalendarEventAttendeeDraft(name, address ?? string.Empty));
        }

        // An event with attendees sends invitations, so it is always reviewed first, like a draft is never sent.
        var isConfident = context.PlanConfidence >= ConfidentEventThreshold && !details.TimeAssumed && attendees.Count == 0;
        if (!isConfident)
        {
            await context.Host.OpenEventEditorAsync(new CalendarEventComposeNavigationArgs
            {
                HasPendingInput = true,
                SelectedCalendarId = calendar.Id,
                Title = details.Title.Trim(),
                Location = details.Location ?? string.Empty,
                IsAllDay = details.IsAllDay,
                StartDate = start,
                EndDate = end,
                NotesHtml = string.IsNullOrWhiteSpace(details.Notes) ? string.Empty : WebUtility.HtmlEncode(details.Notes),
                Attendees = attendees.Where(static attendee => !string.IsNullOrWhiteSpace(attendee.Email)).ToList()
            }).ConfigureAwait(false);

            return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventReview, details.Title.Trim()));
        }

        var composeResult = new CalendarEventComposeResult
        {
            CalendarId = calendar.Id,
            AccountId = account.Id,
            Title = details.Title.Trim(),
            Location = details.Location ?? string.Empty,
            HtmlNotes = string.IsNullOrWhiteSpace(details.Notes) ? string.Empty : WebUtility.HtmlEncode(details.Notes),
            StartDate = start,
            EndDate = end,
            IsAllDay = details.IsAllDay,
            TimeZoneId = TimeZoneInfo.Local.Id,
            ShowAs = calendar.DefaultShowAs,
            SelectedReminders = [],
            Attendees = [],
            Attachments = [],
            Recurrence = string.Empty,
            RecurrenceSummary = string.Empty
        };

        await requestDelegator.ExecuteAsync(new CalendarOperationPreparationRequest(CalendarSynchronizerOperation.CreateEvent, ComposeResult: composeResult)).ConfigureAwait(false);
        var when = details.IsAllDay
            ? start.ToString("D", CultureInfo.CurrentCulture)
            : start.ToString("f", CultureInfo.CurrentCulture);
        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_EventCreated, details.Title.Trim(), when));
    }

    private async Task<List<MailCopy>> GetMailsAsync(StepContext context, CancellationToken cancellationToken)
    {
        var index = context.Step.Command.UsesResultOf is int source ? source - 1 : context.Step.CommandIndex;
        if (context.MailsByCommand.TryGetValue(index, out var cached))
            return cached;

        if (!context.Resolution.MailTargets.TryGetValue(index, out var targets))
            return [];

        var mails = new List<MailCopy>();
        foreach (var target in targets)
            mails.AddRange(await FetchAsync(target, cancellationToken).ConfigureAwait(false));

        context.MailsByCommand[index] = mails;
        return mails;
    }

    private static bool TryGetTargets(StepContext context, out IReadOnlyList<VoiceMailTarget> targets)
    {
        var index = context.Step.Command.UsesResultOf is int source ? source - 1 : context.Step.CommandIndex;
        if (context.Resolution.MailTargets.TryGetValue(index, out var found) && found.Count > 0)
        {
            targets = found;
            return true;
        }

        targets = [];
        return false;
    }

    /// <summary>The selection a step's mails come from: its own, or the one of the command it chains to.</summary>
    private static MailSelection? SourceSelection(VoicePlanPreviewStep step, VoicePlanResolution resolution, IReadOnlyCollection<VoicePlanPreviewGroup>? groups)
    {
        if (step.Command.Mail is { } own)
            return own;

        if (step.Command.UsesResultOf is not int source)
            return null;

        return resolution.Preview.Groups
            .Concat(groups ?? [])
            .SelectMany(static group => group.Steps)
            .FirstOrDefault(candidate => candidate.CommandIndex == source - 1)?.Command.Mail;
    }

    private static MailAccount? ResolveAccount(VoicePlanPreviewGroup group)
        => group.Account ?? group.Candidates.FirstOrDefault();

    private async Task<List<string>> ResolveAddressesAsync(MailAccount account, IEnumerable<string>? names, IReadOnlyList<PersonIdentity>? people)
    {
        var addresses = new List<string>();
        foreach (var raw in names ?? [])
        {
            var name = raw?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (name.Contains('@'))
            {
                addresses.Add(name);
                continue;
            }

            if (await ResolvePersonAsync(name, people).ConfigureAwait(false) is { } known)
            {
                addresses.Add(known);
                continue;
            }

            try
            {
                var candidates = await contactService.ResolveRecipientCandidatesAsync(account.Id, name, 1).ConfigureAwait(false);
                if (candidates?.FirstOrDefault()?.Address is { Length: > 0 } address)
                    addresses.Add(address);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not look up a voice recipient.");
            }
        }

        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsBroad(MailSelection selection)
        => selection.Scope == FolderScope.AllFolders
           || (selection.FolderSpecialType is null or SpecialFolderKind.None or SpecialFolderKind.Other
               && string.IsNullOrWhiteSpace(selection.FolderName)
               && string.IsNullOrWhiteSpace(selection.FolderId));

    /// <summary>A selection that names only a folder: deleting it all empties the folder.</summary>
    private static bool IsFolderOnly(MailSelection selection)
        => !IsBroad(selection)
           && string.IsNullOrWhiteSpace(selection.Sender)
           && string.IsNullOrWhiteSpace(selection.Subject)
           && string.IsNullOrWhiteSpace(selection.Text)
           && selection.DateRange is null
           && selection.ReadStatus == ReadStatusFilter.Any
           && selection.HasAttachments is null
           && selection.IsFlagged is null
           && selection.Limit <= 0;

    private static string CountText(int count)
        => count == 1 ? Translator.VoiceCount_Mail : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Mails, count);

    private static string AccountsLabel(IReadOnlyList<MailAccount> accounts)
        => accounts.Count == 1
            ? AccountTitle(accounts[0])
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_AccountCount, accounts.Count);

    private static string FolderLabel(IReadOnlyList<VoiceMailTarget> targets)
    {
        var folders = targets.SelectMany(static target => target.Query.Folders).ToList();
        var names = folders
            .Select(static folder => string.IsNullOrWhiteSpace(folder.FolderName) ? string.Empty : folder.FolderName)
            .Where(static name => name.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return names.Count is 0 or > 2 ? Translator.VoiceGuard_AllFolders : string.Join(", ", names);
    }

    private static string Subject(MailCopy mail)
        => string.IsNullOrWhiteSpace(mail.Subject) ? Translator.VoiceResult_NoSubject : mail.Subject.Trim();

    private static DateTime ToLocalTime(DateTime wallClock, string? timeZoneId)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return unspecified;

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return zone.Id == TimeZoneInfo.Local.Id
                ? unspecified
                : DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(unspecified, zone, TimeZoneInfo.Local), DateTimeKind.Unspecified);
        }
        catch (Exception)
        {
            return unspecified;
        }
    }

    private sealed record StepContext(
        VoicePlanPreviewGroup Group,
        VoicePlanPreviewStep Step,
        VoicePlanResolution Resolution,
        Dictionary<int, List<MailCopy>> MailsByCommand,
        double PlanConfidence,
        IVoicePlanHost Host);

    private readonly record struct StepResult(string? Line, string? Error, string? Summary)
    {
        public static StepResult None => new(null, null, null);

        public static StepResult Done(string line) => new(line, null, null);

        public static StepResult Failed(string error) => new(null, error, null);
    }
}
