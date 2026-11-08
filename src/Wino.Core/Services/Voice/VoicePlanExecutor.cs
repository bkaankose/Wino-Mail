#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Services.Voice;

public sealed partial class VoicePlanExecutor : IVoicePlanExecutor
{
    private const int PageSize = 200;

    private readonly IMailService mailService;
    private readonly IFolderService folderService;
    private readonly IWinoRequestDelegator requestDelegator;
    private readonly ICalendarService calendarService;
    private readonly ITaskService taskService;
    private readonly IContactService contactService;
    private readonly IMailCategoryService mailCategoryService;
    private readonly IMimeFileService mimeFileService;
    private readonly IPreferencesService preferencesService;
    private readonly IServiceProvider services;

    public VoicePlanExecutor(
        IMailService mailService,
        IFolderService folderService,
        IWinoRequestDelegator requestDelegator,
        ICalendarService calendarService,
        ITaskService taskService,
        IContactService contactService,
        IMailCategoryService mailCategoryService,
        IMimeFileService mimeFileService,
        IPreferencesService preferencesService,
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
        this.services = services;
    }

    /// <summary>
    /// Folders a broad search ("all folders", or no folder named) leaves out: drafts, deleted and junk mail, and the
    /// views that only repeat mails stored elsewhere (Gmail's starred, important, unread and category labels).
    /// </summary>
    private static readonly HashSet<SpecialFolderType> ExcludedFromBroadSearch =
    [
        SpecialFolderType.Draft,
        SpecialFolderType.Deleted,
        SpecialFolderType.Junk,
        SpecialFolderType.Chat,
        SpecialFolderType.Starred,
        SpecialFolderType.Important,
        SpecialFolderType.Unread,
        SpecialFolderType.Category,
        SpecialFolderType.Forums,
        SpecialFolderType.Updates,
        SpecialFolderType.Personal,
        SpecialFolderType.Promotions,
        SpecialFolderType.Social,
        SpecialFolderType.More
    ];

    public async Task<VoicePlanResolution> ResolveAsync(VoicePlanPreview preview, VoiceLocalSelection? selection = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var state = new ResolveState { Selection = selection };
        var outcomes = new Dictionary<VoicePlanPreviewStep, StepOutcome>();

        // Commands run in spoken order across accounts, and a chained command needs its source resolved first.
        var steps = preview.Groups
            .SelectMany(static group => group.Steps.Select(step => (Group: group, Step: step)))
            .OrderBy(static entry => entry.Step.CommandIndex);

        foreach (var (group, original) in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (step, unresolvedToken) = await ExpandResultTokensAsync(original, state, cancellationToken).ConfigureAwait(false);
            var action = step.Command.Action;
            StepOutcome outcome;
            if (unresolvedToken)
                outcome = StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoResultPeople, PlainText(step)));
            else if (step.Command.UsesContext && ActsOnSelection(action) is { } kind)
            {
                (step, outcome) = await ResolveContextStepAsync(step, kind, state).ConfigureAwait(false);
                if (outcome.Issue is null && kind == VoiceContextKind.Mail)
                    outcome = await ResolveMailChangeAsync(step, state, outcome).ConfigureAwait(false);
            }
            else if (VoiceCommand.ActsOnEvents(action))
                outcome = await ResolveEventStepAsync(group, step, state.EventTargets, cancellationToken).ConfigureAwait(false);
            else if (VoiceCommand.ActsOnTasks(action))
                outcome = await ResolveTaskStepAsync(group, step, state, cancellationToken).ConfigureAwait(false);
            else if (VoiceCommand.ActsOnContacts(action))
                outcome = await ResolveContactStepAsync(group, step, state, cancellationToken).ConfigureAwait(false);
            else if (VoiceCommand.ActsOnFolder(action))
                outcome = await ResolveFolderStepAsync(group, step, state, cancellationToken).ConfigureAwait(false);
            else if (action is VoiceAction.CreateTaskList or VoiceAction.RenameTaskList or VoiceAction.DeleteTaskList)
                outcome = await ResolveTaskListStepAsync(group, step, state).ConfigureAwait(false);
            else if (action == VoiceAction.CreateContact)
                outcome = ResolveCreateContactStep(step);
            else
            {
                outcome = await ResolveStepAsync(group, step, state.MailTargets, state.Counts, cancellationToken).ConfigureAwait(false);
                if (outcome.Issue is null)
                    outcome = await ResolveMailChangeAsync(step, state, outcome).ConfigureAwait(false);
            }

            outcomes[original] = ReferenceEquals(step, original) ? outcome : outcome with { Replaced = step };
        }

        var groups = preview.Groups.Select(group => Apply(group, outcomes)).ToList();
        groups = Regroup(groups, step => AccountsOf(step, state));
        return new VoicePlanResolution(new VoicePlanPreview(groups), state.MailTargets.ToDictionary(), state.EventTargets.ToDictionary())
        {
            TaskTargets = state.TaskTargets.ToDictionary(),
            TaskListTargets = state.TaskListTargets.ToDictionary(),
            ContactTargets = state.ContactTargets.ToDictionary(),
            FolderTargets = state.FolderTargets.ToDictionary(),
            CategoryTargets = state.CategoryTargets.ToDictionary()
        };
    }

    /// <summary>The accounts a resolved step touches, from whatever it found.</summary>
    private static List<MailAccount> AccountsOf(VoicePlanPreviewStep step, ResolveState state)
    {
        var index = step.CommandIndex;
        IEnumerable<MailAccount> accounts;
        if (state.EventTargets.TryGetValue(index, out var events))
            accounts = events.Select(static target => target.Account);
        else if (state.MailTargets.TryGetValue(index, out var mails))
            accounts = mails.Where(static target => target.Count > 0).Select(static target => target.Account);
        else if (state.TaskTargets.TryGetValue(index, out var tasks))
            accounts = tasks.Select(static target => target.Account);
        else if (state.ContactTargets.TryGetValue(index, out var contacts))
            accounts = contacts.Select(static target => target.Account).OfType<MailAccount>();
        else if (state.TaskListTargets.TryGetValue(index, out var lists))
            accounts = lists.Select(static target => target.Account);
        else if (state.FolderTargets.TryGetValue(index, out var folders))
            accounts = folders.Select(static target => target.Account);
        else
            return [];

        return accounts.DistinctBy(static account => account.Id).ToList();
    }

    /// <summary>
    /// An "all accounts" step only knows its accounts once its mails or events are found. A step whose matches are all
    /// in one account moves under that account; the "all accounts" group keeps the rest and names only the accounts
    /// they touch.
    /// </summary>
    private static List<VoicePlanPreviewGroup> Regroup(List<VoicePlanPreviewGroup> groups, Func<VoicePlanPreviewStep, List<MailAccount>> accountsOf)
    {
        if (!groups.Any(static group => group.Kind == VoicePlanAccountKind.AllAccounts))
            return groups;

        var result = new List<(VoicePlanPreviewGroup Group, List<VoicePlanPreviewStep> Steps)>();
        foreach (var group in groups.Where(static group => group.Kind != VoicePlanAccountKind.AllAccounts))
            result.Add((group, group.Steps.ToList()));

        foreach (var group in groups.Where(static group => group.Kind == VoicePlanAccountKind.AllAccounts))
        {
            var remaining = new List<VoicePlanPreviewStep>();
            var touched = new List<MailAccount>();
            foreach (var step in group.Steps)
            {
                var accounts = accountsOf(step);
                if (accounts.Count != 1)
                {
                    remaining.Add(step);
                    touched.AddRange(accounts);
                    continue;
                }

                var account = accounts[0];
                var index = result.FindIndex(entry => entry.Group.Kind == VoicePlanAccountKind.Account && entry.Group.Account?.Id == account.Id);
                if (index < 0)
                {
                    result.Add((new VoicePlanPreviewGroup(VoicePlanAccountKind.Account, account, new[] { account }, null, Array.Empty<VoicePlanPreviewStep>(), Array.Empty<VoicePlanPreviewIssue>()), new List<VoicePlanPreviewStep>()));
                    index = result.Count - 1;
                }

                result[index].Steps.Add(step);
            }

            if (remaining.Count == 0 && group.Issues.Count == 0)
                continue;

            var candidates = touched.Count > 0 ? touched.DistinctBy(static account => account.Id).ToList() : group.Candidates.ToList();
            var kind = remaining.Count == 0 ? VoicePlanAccountKind.None : VoicePlanAccountKind.AllAccounts;
            result.Add((group with { Kind = kind, Candidates = candidates }, remaining));
        }

        // Groups follow the order the user spoke in; steps are numbered within their account.
        return result
            .Select(static entry => entry.Group with
            {
                Steps = entry.Steps.OrderBy(static step => step.CommandIndex).Select((step, position) => step with { Number = position + 1 }).ToList()
            })
            .OrderBy(static group => group.Steps.Count > 0 ? group.Steps.Min(static step => step.CommandIndex) : int.MaxValue)
            .ToList();
    }

    private static List<MailAccount> GroupAccounts(VoicePlanPreviewGroup group) => group.Kind switch
    {
        VoicePlanAccountKind.Account when group.Account is { } account => new List<MailAccount> { account },
        VoicePlanAccountKind.AllAccounts => group.Candidates.ToList(),
        _ => new List<MailAccount>()
    };

    /// <summary>What the steps resolved so far act on, by command index, so later steps can chain to them.</summary>
    private sealed class ResolveState
    {
        public Dictionary<int, IReadOnlyList<VoiceMailTarget>> MailTargets { get; } = [];
        public Dictionary<int, int> Counts { get; } = [];
        public Dictionary<int, IReadOnlyList<VoiceEventTarget>> EventTargets { get; } = [];
        public Dictionary<int, IReadOnlyList<VoiceTaskTarget>> TaskTargets { get; } = [];
        public Dictionary<int, IReadOnlyList<VoiceTaskListTarget>> TaskListTargets { get; } = [];
        public Dictionary<int, IReadOnlyList<VoiceContactTarget>> ContactTargets { get; } = [];
        public Dictionary<int, IReadOnlyList<VoiceFolderTarget>> FolderTargets { get; } = [];
        public Dictionary<int, IReadOnlyList<MailCategory>> CategoryTargets { get; } = [];
        public Dictionary<int, List<MailCopy>> Mails { get; } = [];
        public VoiceLocalSelection? Selection { get; init; }
        public HashSet<string>? OwnAddresses { get; set; }
    }

    private async Task<StepOutcome> ResolveStepAsync(
        VoicePlanPreviewGroup group,
        VoicePlanPreviewStep step,
        Dictionary<int, IReadOnlyList<VoiceMailTarget>> targets,
        Dictionary<int, int> counts,
        CancellationToken cancellationToken)
    {
        var command = step.Command;
        if (!VoiceCommand.ActsOnMails(command.Action))
            return StepOutcome.Keep(step.Detail);

        if (command.UsesResultOf is int source)
        {
            var sourceIndex = source - 1;
            if (!targets.TryGetValue(sourceIndex, out var sourceTargets) || !counts.TryGetValue(sourceIndex, out var sourceCount))
                return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ChainedSkipped, PlainText(step)));

            targets[step.CommandIndex] = sourceTargets;
            counts[step.CommandIndex] = sourceCount;
            return StepOutcome.Keep(WithCount(sourceCount, step.Detail));
        }

        var selection = command.Mail;

        // Sending drafts always reads the Drafts folder, whatever else the selection says.
        if (command.Action == VoiceAction.SendDrafts)
        {
            selection = (selection ?? new MailSelection(null, null, FolderScope.Folder, null, null, null, ReadStatusFilter.Any, null, null, null, 0, MailSortOrder.Newest))
                with { FolderSpecialType = SpecialFolderKind.Draft, FolderId = null, FolderName = null, Scope = FolderScope.Folder };
        }

        // Summarize without a selection means the mail open in the app.
        if (selection is null)
            return StepOutcome.Keep(step.Detail);

        if (await ResolvePersonAsync(selection.Sender, command.People).ConfigureAwait(false) is { } senderAddress)
            selection = selection with { Sender = senderAddress };

        var accounts = group.Kind switch
        {
            VoicePlanAccountKind.Account when group.Account is { } account => new List<MailAccount> { account },
            VoicePlanAccountKind.AllAccounts => group.Candidates.ToList(),
            _ => new List<MailAccount>()
        };

        var found = new List<VoiceMailTarget>();
        string? problem = null;
        foreach (var account in accounts)
        {
            try
            {
                var (folders, folderProblem) = await ResolveFoldersAsync(account, selection).ConfigureAwait(false);
                if (folders is null)
                {
                    problem ??= folderProblem;
                    continue;
                }

                var target = CreateTarget(account, folders, selection, command.Action);
                found.Add(target with { Count = await CountAsync(target, cancellationToken).ConfigureAwait(false) });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not find the mails for a voice step in account {AccountId}.", account.Id);
            }
        }

        var total = found.Sum(static target => target.Count);
        if (found.Count == 0 && problem is not null)
            return StepOutcome.Drop(problem);
        if (total == 0)
            return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoMails, PlainText(step)));

        targets[step.CommandIndex] = found.Where(static target => target.Count > 0).ToList();
        counts[step.CommandIndex] = total;
        return StepOutcome.Keep(WithCount(total, step.Detail));
    }

    private async Task<(IReadOnlyList<IMailItemFolder>? Folders, string? Problem)> ResolveFoldersAsync(MailAccount account, MailSelection selection)
    {
        var all = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false) ?? [];
        var accountName = AccountTitle(account);

        List<MailItemFolder> roots;
        var byRemoteId = string.IsNullOrWhiteSpace(selection.FolderId)
            ? null
            : all.FirstOrDefault(folder => string.Equals(folder.RemoteFolderId, selection.FolderId, StringComparison.Ordinal));

        if (byRemoteId is null && (selection.Scope == FolderScope.AllFolders || (selection.FolderSpecialType is null or SpecialFolderKind.None or SpecialFolderKind.Other && string.IsNullOrWhiteSpace(selection.FolderName))))
        {
            var broad = all.Where(static folder => !folder.IsHidden && !ExcludedFromBroadSearch.Contains(folder.SpecialFolderType)).ToList();
            return broad.Count == 0
                ? (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolders, accountName))
                : (broad.Cast<IMailItemFolder>().ToList(), null);
        }

        if (byRemoteId is not null)
        {
            roots = [byRemoteId];
        }
        else if (ToSpecialFolderType(selection.FolderSpecialType) is { } type)
        {
            var special = all.FirstOrDefault(folder => folder.SpecialFolderType == type);
            if (special is null)
                return (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSpecialFolder, accountName, SpecialFolderName(selection.FolderSpecialType)));

            roots = [special];
        }
        else if (!string.IsNullOrWhiteSpace(selection.FolderName))
        {
            var name = selection.FolderName.Trim();
            roots = all.Where(folder => SameName(folder, name, exact: true)).ToList();
            if (roots.Count == 0)
                roots = all.Where(folder => SameName(folder, name, exact: false)).ToList();
            if (roots.Count == 0)
                return (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolder, name, accountName));
        }
        else
        {
            return (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolder, selection.FolderId ?? string.Empty, accountName));
        }

        if (selection.Scope == FolderScope.Subfolders)
            roots = WithSubfolders(roots, all);

        return (roots.DistinctBy(static folder => folder.Id).Cast<IMailItemFolder>().ToList(), null);
    }

    private static List<MailItemFolder> WithSubfolders(List<MailItemFolder> roots, List<MailItemFolder> all)
    {
        var result = new List<MailItemFolder>(roots);
        var queue = new Queue<MailItemFolder>(roots);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in all.Where(folder => !string.IsNullOrEmpty(folder.ParentRemoteFolderId) && folder.ParentRemoteFolderId == parent.RemoteFolderId))
            {
                if (result.Any(existing => existing.Id == child.Id))
                    continue;

                result.Add(child);
                queue.Enqueue(child);
            }
        }

        return result;
    }

    private static bool SameName(MailItemFolder folder, string name, bool exact)
    {
        foreach (var value in new[] { folder.DisplayName, folder.FolderName })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (exact ? string.Equals(value.Trim(), name, StringComparison.CurrentCultureIgnoreCase) : value.Contains(name, StringComparison.CurrentCultureIgnoreCase))
                return true;
        }

        return false;
    }

    private static VoiceMailTarget CreateTarget(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSelection selection, VoiceAction action)
    {
        var query = new MailListInitializationOptions(
            folders,
            FilterOptionType.All,
            SortingOptionType.ReceiveDate,
            CreateThreads: false,
            IsFocusedOnly: null,
            SearchQuery: Clean(selection.Text),
            DeduplicateByServerId: true)
        {
            Sender = Clean(selection.Sender),
            Subject = Clean(selection.Subject),
            ReceivedAfterUtc = selection.DateRange is { } range ? ToUtc(range.StartLocal) : null,
            ReceivedBeforeUtc = selection.DateRange is { } until ? ToUtc(until.EndLocal) : null,
            RequireAttachments = selection.HasAttachments == true,
            ReadStatus = selection.ReadStatus switch
            {
                ReadStatusFilter.Unread => MailReadStatusFilter.Unread,
                ReadStatusFilter.Read => MailReadStatusFilter.Read,
                _ => MailReadStatusFilter.All
            },
            RequireFlagged = selection.IsFlagged == true,
            ExcludeDrafts = action != VoiceAction.SendDrafts && selection.FolderSpecialType != SpecialFolderKind.Draft
        };

        return new VoiceMailTarget(
            account,
            query,
            Math.Max(0, selection.Limit),
            selection.Order == MailSortOrder.Oldest,
            WithoutAttachments: selection.HasAttachments == false,
            NotFlagged: selection.IsFlagged == false,
            Count: 0,
            SenderKind: selection.SenderKind);
    }

    private async Task<int> CountAsync(VoiceMailTarget target, CancellationToken cancellationToken)
    {
        if (target.Items is { } items)
            return items.Count;
        if (target.WithoutAttachments || target.NotFlagged || target.SenderKind != MailSenderKind.Any)
            return (await FetchAsync(target, cancellationToken).ConfigureAwait(false)).Count;

        var matches = await mailService.CountMailsAsync(target.Query, cancellationToken).ConfigureAwait(false);
        return target.Limit > 0 ? Math.Min(matches, target.Limit) : matches;
    }

    /// <summary>
    /// Reads every mail a target matches, page by page, newest first, then applies what the query can't express:
    /// no attachments, not flagged, and the newest or oldest <see cref="VoiceMailTarget.Limit"/>.
    /// </summary>
    private async Task<List<MailCopy>> FetchAsync(VoiceMailTarget target, CancellationToken cancellationToken)
    {
        if (target.Items is { } items)
            return items.ToList();

        var options = target.Query with { Take = PageSize };
        var mails = new List<MailCopy>();
        MailFetchCursor? cursor = null;

        while (true)
        {
            var page = cursor is null
                ? await mailService.FetchMailPageAsync(options, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await mailService.FetchMailPageAsync(options, cursor, cancellationToken).ConfigureAwait(false);
            mails.AddRange(page.Items);
            if (!page.HasMore || page.NextCursor is null)
                break;

            cursor = page.NextCursor;
        }

        IEnumerable<MailCopy> result = mails
            .DistinctBy(static mail => mail.UniqueId)
            .Where(mail => !target.WithoutAttachments || !mail.HasAttachments)
            .Where(mail => !target.NotFlagged || !mail.IsFlagged)
            .Where(mail => MailSenderClassifier.Matches(target.SenderKind, mail.FromAddress, mail.FromName));

        result = target.OldestFirst
            ? result.OrderBy(static mail => mail.CreationDate)
            : result.OrderByDescending(static mail => mail.CreationDate);

        if (target.Limit > 0)
            result = result.Take(target.Limit);

        return result.ToList();
    }

    private static VoicePlanPreviewGroup Apply(VoicePlanPreviewGroup group, Dictionary<VoicePlanPreviewStep, StepOutcome> outcomes)
    {
        var steps = new List<VoicePlanPreviewStep>();
        var issues = new List<VoicePlanPreviewIssue>(group.Issues);

        foreach (var step in group.Steps)
        {
            var outcome = outcomes.TryGetValue(step, out var value) ? value : StepOutcome.Keep(step.Detail);
            var current = outcome.Replaced ?? step;
            if (outcome.Issue is { } issue)
            {
                issues.Add(new VoicePlanPreviewIssue(issue, current.Command));
                continue;
            }

            steps.Add(current with { Number = steps.Count + 1, Detail = outcome.Detail });
        }

        return group with { Steps = steps, Issues = issues };
    }

    private static string WithCount(int count, string? detail)
    {
        var found = string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Found, count);
        return string.IsNullOrWhiteSpace(detail) ? found : $"{found} · {detail}";
    }

    private static string PlainText(VoicePlanPreviewStep step) => step.MarkedText.Replace("**", string.Empty).Trim();

    private static string Clean(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();

    private static DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    private static string AccountTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : account.Address ?? string.Empty;

    private static SpecialFolderType? ToSpecialFolderType(SpecialFolderKind? kind) => kind switch
    {
        SpecialFolderKind.Inbox => SpecialFolderType.Inbox,
        SpecialFolderKind.Starred => SpecialFolderType.Starred,
        SpecialFolderKind.Important => SpecialFolderType.Important,
        SpecialFolderKind.Sent => SpecialFolderType.Sent,
        SpecialFolderKind.Draft => SpecialFolderType.Draft,
        SpecialFolderKind.Archive => SpecialFolderType.Archive,
        SpecialFolderKind.Deleted => SpecialFolderType.Deleted,
        SpecialFolderKind.Junk => SpecialFolderType.Junk,
        _ => null
    };

    private static string SpecialFolderName(SpecialFolderKind? kind) => kind switch
    {
        SpecialFolderKind.Inbox => Translator.VoiceStep_Folder_Inbox,
        SpecialFolderKind.Starred => Translator.VoiceStep_Folder_Starred,
        SpecialFolderKind.Important => Translator.VoiceStep_Folder_Important,
        SpecialFolderKind.Sent => Translator.VoiceStep_Folder_Sent,
        SpecialFolderKind.Draft => Translator.VoiceStep_Folder_Drafts,
        SpecialFolderKind.Archive => Translator.VoiceStep_Folder_Archive,
        SpecialFolderKind.Deleted => Translator.VoiceStep_Folder_Deleted,
        SpecialFolderKind.Junk => Translator.VoiceStep_Folder_Junk,
        _ => string.Empty
    };

    /// <param name="Replaced">The step with its <c>@result</c> people filled in, when it had any.</param>
    private readonly record struct StepOutcome(string? Detail, string? Issue, VoicePlanPreviewStep? Replaced = null)
    {
        public static StepOutcome Keep(string? detail) => new(detail, null);

        public static StepOutcome Drop(string issue) => new(null, issue);
    }
}
