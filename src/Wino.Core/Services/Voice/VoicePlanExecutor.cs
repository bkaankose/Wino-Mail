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

    public async Task<VoicePlanResolution> ResolveAsync(VoicePlanPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var targets = new Dictionary<int, IReadOnlyList<VoiceMailTarget>>();
        var counts = new Dictionary<int, int>();
        var eventTargets = new Dictionary<int, IReadOnlyList<VoiceEventTarget>>();
        var outcomes = new Dictionary<VoicePlanPreviewStep, StepOutcome>();

        // Commands run in spoken order across accounts, and a chained command needs its source resolved first.
        var steps = preview.Groups
            .SelectMany(static group => group.Steps.Select(step => (Group: group, Step: step)))
            .OrderBy(static entry => entry.Step.CommandIndex);

        foreach (var (group, step) in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes[step] = VoiceCommand.ActsOnEvents(step.Command.Action)
                ? await ResolveEventStepAsync(group, step, eventTargets, cancellationToken).ConfigureAwait(false)
                : await ResolveStepAsync(group, step, targets, counts, cancellationToken).ConfigureAwait(false);
        }

        var groups = preview.Groups.Select(group => Apply(group, outcomes)).ToList();
        groups = Regroup(groups, targets, eventTargets);
        return new VoicePlanResolution(new VoicePlanPreview(groups), targets, eventTargets);
    }

    /// <summary>
    /// An "all accounts" step only knows its accounts once its mails or events are found. A step whose matches are all
    /// in one account moves under that account; the "all accounts" group keeps the rest and names only the accounts
    /// they touch.
    /// </summary>
    private static List<VoicePlanPreviewGroup> Regroup(
        List<VoicePlanPreviewGroup> groups,
        Dictionary<int, IReadOnlyList<VoiceMailTarget>> mailTargets,
        Dictionary<int, IReadOnlyList<VoiceEventTarget>> eventTargets)
    {
        if (!groups.Any(static group => group.Kind == VoicePlanAccountKind.AllAccounts))
            return groups;

        List<MailAccount> AccountsOf(VoicePlanPreviewStep step)
        {
            if (eventTargets.TryGetValue(step.CommandIndex, out var events))
                return events.Select(static target => target.Account).DistinctBy(static account => account.Id).ToList();
            if (mailTargets.TryGetValue(step.CommandIndex, out var mails))
                return mails.Where(static target => target.Count > 0).Select(static target => target.Account).DistinctBy(static account => account.Id).ToList();
            return [];
        }

        var result = new List<(VoicePlanPreviewGroup Group, List<VoicePlanPreviewStep> Steps)>();
        foreach (var group in groups.Where(static group => group.Kind != VoicePlanAccountKind.AllAccounts))
            result.Add((group, group.Steps.ToList()));

        foreach (var group in groups.Where(static group => group.Kind == VoicePlanAccountKind.AllAccounts))
        {
            var remaining = new List<VoicePlanPreviewStep>();
            var touched = new List<MailAccount>();
            foreach (var step in group.Steps)
            {
                var accounts = AccountsOf(step);
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

        // Summarize without a selection means the mail open in the app.
        if (command.Mail is not { } selection)
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

                var target = CreateTarget(account, folders, selection);
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

    private static VoiceMailTarget CreateTarget(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSelection selection)
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
            ExcludeDrafts = true
        };

        return new VoiceMailTarget(
            account,
            query,
            Math.Max(0, selection.Limit),
            selection.Order == MailSortOrder.Oldest,
            WithoutAttachments: selection.HasAttachments == false,
            NotFlagged: selection.IsFlagged == false,
            Count: 0);
    }

    private async Task<int> CountAsync(VoiceMailTarget target, CancellationToken cancellationToken)
    {
        if (target.WithoutAttachments || target.NotFlagged)
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
            .Where(mail => !target.NotFlagged || !mail.IsFlagged);

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
            if (outcome.Issue is { } issue)
            {
                issues.Add(new VoicePlanPreviewIssue(issue, step.Command));
                continue;
            }

            steps.Add(step with { Number = steps.Count + 1, Detail = outcome.Detail });
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

    private readonly record struct StepOutcome(string? Detail, string? Issue)
    {
        public static StepOutcome Keep(string? detail) => new(detail, null);

        public static StepOutcome Drop(string issue) => new(null, issue);
    }
}
