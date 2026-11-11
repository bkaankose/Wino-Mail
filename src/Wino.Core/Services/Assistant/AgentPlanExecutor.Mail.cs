#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Serilog;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Misc;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Mail;
using Wino.Messaging.Server;

namespace Wino.Core.Services.Assistant;

/// <summary>
/// Mail steps: the device reads the mails a <see cref="MailQuery"/> describes (folder ids, resolved sender addresses
/// and domains, words, state flags, a period, a limit) or the exact mails a selection names, and changes them through
/// the same requests the mail list queues. Drafts are created and opened in the composer, never sent; only
/// <see cref="AgentAction.SendDrafts"/> sends, and only existing drafts.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    private const int MaxAnalyzedMails = 25;
    private const int MaxCodeMails = 5;
    private const int MaxRecipientFilterMails = 200;

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

    // ---- targets -------------------------------------------------------------------------------------------------

    /// <summary>The mails a step acts on, read once per step: its query, its items, or the result of the step it chains to.</summary>
    private async Task<List<MailCopy>> ResolveMailsAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (run.Mails.TryGetValue(step.N, out var cached))
            return cached;

        List<MailCopy> mails;
        switch (step.Target)
        {
            case StepTarget.Mails query:
                mails = await FetchMailsAsync(query.AccountId, query.Query, step.Action, run, ct).ConfigureAwait(false);
                break;

            case StepTarget.MailItems items:
                mails = await LoadMailsAsync(items.Items, ct).ConfigureAwait(false);
                break;

            case StepTarget.FromStep { Part: "items" } from:
                mails = run.Mails.TryGetValue(from.N, out var source)
                    ? source.ToList()
                    : await LoadMailsAsync(TouchedOf(run, from.N, ItemKind.Mail), ct).ConfigureAwait(false);
                break;

            case StepTarget.FromStep from:
            {
                // "Mails from the people in this event": the people of the earlier step become the senders.
                var people = await PeopleOfStepAsync(run, from.N, from.Part, ct).ConfigureAwait(false);
                mails = people.Count == 0
                    ? []
                    : await FetchMailsAsync(null, MailQuery.Empty with { SenderAddresses = people.Select(static person => person.Address).ToList() }, step.Action, run, ct).ConfigureAwait(false);
                break;
            }

            default:
                mails = [];
                break;
        }

        run.Mails[step.N] = mails;
        return mails;
    }

    private async Task<List<MailCopy>> LoadMailsAsync(IEnumerable<ItemRef> items, CancellationToken ct)
    {
        var mails = new List<MailCopy>();
        foreach (var item in items.Where(static item => item.Kind is ItemKind.Mail or ItemKind.Draft))
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(item.Id, out var uniqueId))
                continue;

            try
            {
                if (await mailService.GetSingleMailItemAsync(uniqueId).ConfigureAwait(false) is { AssignedAccount: not null } mail)
                    mails.Add(mail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(ex, "Could not load a mail of an assistant step.");
            }
        }

        return mails.DistinctBy(static mail => mail.UniqueId).ToList();
    }

    /// <summary>
    /// Reads every mail a query matches in the account (every account when none is named), page by page, then applies
    /// what the mail list query cannot express: several senders, domains and sender words, a negated sender, recipients,
    /// the sender kind, and the newest or oldest <see cref="MailQuery.Limit"/>.
    /// </summary>
    private async Task<List<MailCopy>> FetchMailsAsync(Guid? accountId, MailQuery query, AgentAction action, PlanRun run, CancellationToken ct)
    {
        var accounts = await AccountsAsync(run).ConfigureAwait(false);
        if (accountId is Guid one)
            accounts = accounts.Where(account => account.Id == one).ToList();

        var mails = new List<MailCopy>();
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var folders = await FoldersForQueryAsync(account, query, action).ConfigureAwait(false);
                if (folders.Count == 0)
                    continue;

                var options = CreateOptions(folders, query, action);
                mails.AddRange(await FetchPagesAsync(options, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not find the mails for an assistant step in account {AccountId}.", account.Id);
            }
        }

        IEnumerable<MailCopy> result = mails
            .DistinctBy(static mail => mail.UniqueId)
            .Where(mail => mail.AssignedAccount is not null)
            .Where(mail => MatchesSender(mail, query))
            .Where(mail => MatchesSenderKind(mail, query.SenderKind));

        if (query.Flags.HasFlag(MailStateFlags.Draft))
            result = result.Where(static mail => mail.IsDraft);

        result = query.Order == ResultOrder.Oldest
            ? result.OrderBy(static mail => mail.CreationDate)
            : result.OrderByDescending(static mail => mail.CreationDate);

        var list = result.ToList();
        if (query.RecipientAddresses.Count > 0)
            list = await FilterByRecipientsAsync(list, query.RecipientAddresses, ct).ConfigureAwait(false);

        return query.Limit > 0 ? list.Take(query.Limit).ToList() : list;
    }

    /// <summary>
    /// The folders of an account a query reads: the named ones that belong to it, else every visible folder minus the
    /// broad-search exclusions. Sending drafts and a draft-state query always read the Drafts folder.
    /// </summary>
    private async Task<List<IMailItemFolder>> FoldersForQueryAsync(MailAccount account, MailQuery query, AgentAction action)
    {
        var all = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false) ?? [];
        if (action == AgentAction.SendDrafts || query.Flags.HasFlag(MailStateFlags.Draft))
            return all.Where(static folder => folder.SpecialFolderType == SpecialFolderType.Draft).Cast<IMailItemFolder>().ToList();

        if (query.FolderIds.Count > 0)
        {
            var wanted = query.FolderIds.ToHashSet();
            return all.Where(folder => wanted.Contains(folder.Id)).Cast<IMailItemFolder>().ToList();
        }

        return all.Where(static folder => !folder.IsHidden && !ExcludedFromBroadSearch.Contains(folder.SpecialFolderType)).Cast<IMailItemFolder>().ToList();
    }

    private static MailListInitializationOptions CreateOptions(IReadOnlyList<IMailItemFolder> folders, MailQuery query, AgentAction action)
    {
        // One plain sender address narrows the query in the database; anything richer is matched in memory.
        var singleSender = query.SenderAddresses.Count == 1 && query.SenderDomains.Count == 0 && string.IsNullOrWhiteSpace(query.SenderWords) && !query.Negated
            ? query.SenderAddresses[0]
            : null;

        return new MailListInitializationOptions(
            folders,
            FilterOptionType.All,
            SortingOptionType.ReceiveDate,
            CreateThreads: false,
            IsFocusedOnly: null,
            SearchQuery: Clean(query.Text),
            DeduplicateByServerId: true,
            Take: PageSize)
        {
            Sender = Clean(singleSender),
            ReceivedAfterUtc = query.From is { } from ? ToUtc(from) : null,
            ReceivedBeforeUtc = query.To is { } to ? ToUtc(to) : null,
            RequireAttachments = query.Flags.HasFlag(MailStateFlags.HasAttachments),
            ReadStatus = query.Flags.HasFlag(MailStateFlags.Unread) ? MailReadStatusFilter.Unread
                : query.Flags.HasFlag(MailStateFlags.Read) ? MailReadStatusFilter.Read
                : MailReadStatusFilter.All,
            RequireFlagged = query.Flags.HasFlag(MailStateFlags.Flagged),
            ExcludeDrafts = action != AgentAction.SendDrafts && !query.Flags.HasFlag(MailStateFlags.Draft)
        };
    }

    private async Task<List<MailCopy>> FetchPagesAsync(MailListInitializationOptions options, CancellationToken ct)
    {
        var mails = new List<MailCopy>();
        MailFetchCursor? cursor = null;

        while (true)
        {
            var page = cursor is null
                ? await mailService.FetchMailPageAsync(options, cancellationToken: ct).ConfigureAwait(false)
                : await mailService.FetchMailPageAsync(options, cursor, ct).ConfigureAwait(false);
            mails.AddRange(page.Items);
            if (!page.HasMore || page.NextCursor is null)
                break;

            cursor = page.NextCursor;
        }

        return mails;
    }

    /// <summary>
    /// Whether a mail's sender is one the query names: an address, a domain, or the words of a sender nothing resolved
    /// (all of them in the sender's name or address). A negated query keeps the mails whose sender is not named.
    /// </summary>
    private static bool MatchesSender(MailCopy mail, MailQuery query)
    {
        var hasCriteria = query.SenderAddresses.Count > 0 || query.SenderDomains.Count > 0 || !string.IsNullOrWhiteSpace(query.SenderWords);
        if (!hasCriteria)
            return true;

        var address = mail.FromAddress?.Trim() ?? string.Empty;
        var domain = address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..] : string.Empty;
        var matches = query.SenderAddresses.Any(wanted => SameAddress(wanted, address))
            || query.SenderDomains.Any(wanted => DomainMatches(domain, wanted))
            || (!string.IsNullOrWhiteSpace(query.SenderWords) && FitsWords(query.SenderWords, mail.FromName, address));

        return query.Negated ? !matches : matches;
    }

    private static bool DomainMatches(string domain, string wanted)
    {
        var trimmed = wanted.Trim().TrimStart('@');
        return domain.Length > 0 && trimmed.Length > 0
            && (string.Equals(domain, trimmed, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Recipients live in the stored MIME, so the match is bounded and a mail that is not downloaded is left out.</summary>
    private async Task<List<MailCopy>> FilterByRecipientsAsync(List<MailCopy> mails, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        var kept = new List<MailCopy>();
        foreach (var mail in mails.Take(MaxRecipientFilterMails))
        {
            if (await ReadMimeAsync(mail, ct).ConfigureAwait(false) is not { } message)
                continue;

            var addresses = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes).Select(static mailbox => mailbox.Address);
            if (addresses.Any(address => recipients.Any(wanted => SameAddress(wanted, address))))
                kept.Add(mail);
        }

        return kept;
    }

    /// <summary>
    /// The device's sender-kind enum (what <see cref="MailSenderClassifier"/> and the search criteria use) has the same
    /// member names as the runtime's <see cref="SenderKind"/>: Any, Person, Service, Newsletter. Its type is taken from
    /// <see cref="MailSearchCriteria.Empty"/> rather than named, so this file does not depend on where the contracts
    /// rewrite puts that enum.
    /// </summary>
    private static T ToDeviceSenderKind<T>(T sample, SenderKind kind) where T : struct, Enum
        => Enum.TryParse<T>(kind.ToString(), ignoreCase: true, out var parsed) ? parsed : sample;

    private static bool MatchesSenderKind(MailCopy mail, SenderKind kind)
        => kind == SenderKind.Any || MailSenderClassifier.Matches(ToDeviceSenderKind(MailSearchCriteria.Empty.SenderKind, kind), mail.FromAddress, mail.FromName);

    private static ItemRef Ref(MailCopy mail)
        => new(mail.IsDraft ? ItemKind.Draft : ItemKind.Mail, mail.AssignedAccount?.Id, mail.UniqueId.ToString());

    private static List<ItemRef> Refs(IEnumerable<MailCopy> mails) => mails.Select(Ref).ToList();

    // ---- operations ----------------------------------------------------------------------------------------------

    private async Task<StepOutcome> RunMailOperationAsync(ResolvedStep step, PlanRun run, MailOperation operation, string template, CancellationToken ct)
    {
        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        // The plan was confirmed in the bubble, so the hard delete dialog is not asked again.
        if (await QueueMailOperationAsync(operation, mails, null).ConfigureAwait(false) is { } error)
            return Failed(step, error);

        return Done(step, Refs(mails), string.Format(CultureInfo.CurrentCulture, template, CountText(mails.Count)));
    }

    /// <summary>Queues a mail operation the way the mail list does; returns the error, or null when it was queued.</summary>
    private async Task<string?> QueueMailOperationAsync(MailOperation operation, List<MailCopy> mails, MailItemFolder? target)
    {
        var request = target is null
            ? new MailOperationPreperationRequest(operation, mails, toggleExecution: false, ignoreHardDeleteProtection: true)
            : new MailOperationPreperationRequest(operation, mails, toggleExecution: false, moveTargetFolder: target, ignoreHardDeleteProtection: true);
        var result = await requestDelegator.ExecuteWithResultAsync(request).ConfigureAwait(false);
        return result.Error?.Message;
    }

    private async Task<StepOutcome> MoveToFolderAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (step.Args is not StepArgs.MailChangeArgs { DestinationFolderId: Guid destinationId })
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Folder));

        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        var destination = await folderService.GetFolderAsync(destinationId).ConfigureAwait(false);
        if (destination is not { IsMoveTarget: true })
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Folder));

        // A folder belongs to one account; mails of other accounts cannot move into it.
        var moving = mails.Where(mail => mail.AssignedAccount!.Id == destination.MailAccountId && mail.FolderId != destination.Id).ToList();
        if (moving.Count == 0)
            return Failed(step, Translator.VoiceResult_NoMailsLeft);

        if (await QueueMailOperationAsync(MailOperation.Move, moving, destination).ConfigureAwait(false) is { } error)
            return Failed(step, error);

        return Done(step, Refs(moving), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Moved, CountText(moving.Count), Plain(destination.ResolvedDisplayName ?? string.Empty)));
    }

    private async Task<StepOutcome> MoveToInboxTabAsync(ResolvedStep step, PlanRun run, bool focused, CancellationToken ct)
    {
        var mails = (await ResolveMailsAsync(step, run, ct).ConfigureAwait(false))
            .Where(static mail => mail.AssignedAccount?.ProviderType == MailProviderType.Outlook)
            .ToList();
        if (mails.Count == 0)
            return Failed(step, Translator.VoiceIssue_FocusedOutlookOnly);

        if (step.Args is StepArgs.MailChangeArgs { Always: true })
        {
            // The rule is per sender, so one mail of each sender is enough to set it.
            var bySender = mails.DistinctBy(static mail => mail.FromAddress?.Trim().ToLowerInvariant()).ToList();
            if (await QueueMailOperationAsync(focused ? MailOperation.AlwaysMoveToFocused : MailOperation.AlwaysMoveToOther, bySender, null).ConfigureAwait(false) is { } alwaysError)
                return Failed(step, alwaysError);

            var senders = string.Join(", ", bySender.Select(static mail => Plain(SenderName(mail))).Take(3));
            return Done(step, Refs(mails), string.Format(CultureInfo.CurrentCulture, focused ? Translator.VoiceResult_AlwaysFocused : Translator.VoiceResult_AlwaysOther, senders));
        }

        if (await QueueMailOperationAsync(focused ? MailOperation.MoveToFocused : MailOperation.MoveToOther, mails, null).ConfigureAwait(false) is { } error)
            return Failed(step, error);

        return Done(step, Refs(mails), string.Format(CultureInfo.CurrentCulture, focused ? Translator.VoiceResult_MovedToFocused : Translator.VoiceResult_MovedToOther, CountText(mails.Count)));
    }

    private async Task<StepOutcome> PinMailsAsync(ResolvedStep step, PlanRun run, bool pin, CancellationToken ct)
    {
        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        await mailService.ChangePinnedStatusAsync(mails.Select(static mail => mail.UniqueId).Distinct().ToList(), pin).ConfigureAwait(false);
        return Done(step, Refs(mails), string.Format(CultureInfo.CurrentCulture, pin ? Translator.VoiceResult_Pinned : Translator.VoiceResult_Unpinned, CountText(mails.Count)));
    }

    /// <summary>
    /// Assigns or removes a category like the mail list does: stored locally first, then sent to Outlook, the only
    /// provider whose categories live on the server. A category belongs to one account; other accounts' mails are left out.
    /// </summary>
    private async Task<StepOutcome> CategorizeMailsAsync(ResolvedStep step, PlanRun run, bool assign, CancellationToken ct)
    {
        if (step.Args is not StepArgs.MailChangeArgs { CategoryId: Guid categoryId })
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Category));

        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        var category = await mailCategoryService.GetCategoryAsync(categoryId).ConfigureAwait(false);
        if (category is null)
            return Failed(step, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Category));

        var unique = mails.Where(mail => mail.AssignedAccount!.Id == category.MailAccountId).DistinctBy(static mail => mail.UniqueId).ToList();
        if (unique.Count == 0)
            return Failed(step, Translator.VoiceResult_NoMailsLeft);

        var ids = unique.Select(static mail => mail.UniqueId).ToList();
        if (assign)
            await mailCategoryService.AssignCategoryAsync(category.Id, ids).ConfigureAwait(false);
        else
            await mailCategoryService.UnassignCategoryAsync(category.Id, ids).ConfigureAwait(false);

        if (unique[0].AssignedAccount!.ProviderType == MailProviderType.Outlook)
        {
            var requests = new List<IRequestBase>();
            foreach (var mail in unique)
            {
                var names = await mailCategoryService.GetCategoryNamesForMailAsync(mail.UniqueId).ConfigureAwait(false) ?? [];
                requests.Add(new MailCategoryAssignmentRequest(mail, category.Id, category.Name, names, assign));
            }

            await requestDelegator.ExecuteAsync(category.MailAccountId, requests).ConfigureAwait(false);
        }

        return Done(step, Refs(unique), string.Format(CultureInfo.CurrentCulture, assign ? Translator.VoiceResult_Categorized : Translator.VoiceResult_Uncategorized, CountText(unique.Count), Plain(category.Name ?? string.Empty)));
    }

    // ---- search and reading --------------------------------------------------------------------------------------

    /// <summary>Opens the mail list on the query, or the mail itself when exactly one matches. The matches are the step's result.</summary>
    private async Task<StepOutcome> OpenSearchAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);
        if (Host is not { } host)
            return Failed(step, Translator.VoiceResult_LaunchUnavailable);

        if (mails.Count == 1)
        {
            await host.OpenMailAsync(mails[0]).ConfigureAwait(false);
            return Done(step, Refs(mails), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_OpenedMail, Subject(mails[0])));
        }

        var account = mails[0].AssignedAccount!;
        var query = step.Target is StepTarget.Mails target ? target.Query : MailQuery.Empty;
        var folders = query.FolderIds.Count > 0
            ? await folderService.GetFoldersByIdsAsync(query.FolderIds.ToList(), ct).ConfigureAwait(false) ?? []
            : mails.Select(static mail => mail.AssignedFolder).OfType<MailItemFolder>().DistinctBy(static folder => folder.Id).ToList();
        var accountIds = mails.Select(static mail => mail.AssignedAccount!.Id).Distinct().ToList();
        var singleSender = query.SenderAddresses.Count == 1 && query.SenderDomains.Count == 0 && !query.Negated ? query.SenderAddresses[0] : string.Empty;

        var criteria = new MailSearchCriteria(
            Clean(query.Text),
            SearchMode.Local,
            query.FolderIds.Count > 0 ? MailSearchScope.CurrentFolder : MailSearchScope.AllFolders,
            MailSearchReach.DownloadedOnly,
            Clean(singleSender),
            string.Empty,
            query.From is { } from ? ToUtc(from) : null,
            query.To is { } to ? ToUtc(to) : null,
            query.Flags.HasFlag(MailStateFlags.HasAttachments),
            query.Flags.HasFlag(MailStateFlags.Unread) ? MailReadStatusFilter.Unread : query.Flags.HasFlag(MailStateFlags.Read) ? MailReadStatusFilter.Read : MailReadStatusFilter.All,
            query.Flags.HasFlag(MailStateFlags.Flagged),
            folders.Select(static folder => folder.Id).ToList(),
            accountIds)
        {
            SenderKind = ToDeviceSenderKind(MailSearchCriteria.Empty.SenderKind, query.SenderKind)
        };

        await host.OpenSearchAsync(account, folders.Cast<IMailItemFolder>().ToList(), criteria).ConfigureAwait(false);
        return Done(step, Refs(mails), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Showing, CountText(mails.Count)));
    }

    /// <summary>Summarizes the first mail with Wino Intelligence; the summary itself is the detail.</summary>
    private async Task<StepOutcome> SummarizeAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (step.Target is StepTarget.None)
            return Failed(step, Translator.VoiceResult_SummarizeOpenMail);

        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.FirstOrDefault() is not { AssignedAccount: { } account } mail)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        if (services.GetService<IWinoIntelligenceCoordinator>() is not { } coordinator
            || await CreateIntelligenceContextAsync(mail, account, ct).ConfigureAwait(false) is not { } context)
            return Failed(step, Translator.VoiceResult_SummaryUnavailable);

        var result = await coordinator.SummarizeAsync(context, Guid.NewGuid(), ct).ConfigureAwait(false);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Value))
            return Failed(step, result.Error ?? Translator.VoiceResult_SummaryUnavailable);

        return Done(step, [Ref(mail)], result.Value.Trim());
    }

    /// <summary>
    /// Translates the first mail with Wino Intelligence; the translation is the detail and is also cached for the mail.
    /// The mail opens so the user can read the original next to it.
    /// </summary>
    private async Task<StepOutcome> TranslateAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.FirstOrDefault() is not { AssignedAccount: { } account } mail)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        if (services.GetService<IWinoIntelligenceCoordinator>() is not { } coordinator
            || await CreateIntelligenceContextAsync(mail, account, ct).ConfigureAwait(false) is not { } context)
            return Failed(step, Translator.VoiceResult_TranslationUnavailable);

        var language = step.Args is StepArgs.MailChangeArgs { Language: { Length: > 0 } requested }
            ? requested.Trim()
            : preferencesService.AiDefaultTranslationLanguageCode;
        var result = await coordinator.TranslateAsync(context, Guid.NewGuid(), null, language, ct).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
            return Failed(step, result.Error ?? Translator.VoiceResult_TranslationUnavailable);

        var text = string.Join(Environment.NewLine, result.Value.Translations.Select(static segment => segment.Text).Where(static segment => !string.IsNullOrWhiteSpace(segment))).Trim();
        if (Host is { } host)
            await host.OpenMailAsync(mail).ConfigureAwait(false);

        return Done(step, [Ref(mail)], text.Length > 0
            ? text
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Translated, Subject(mail), LanguageName(language)));
    }

    private async Task<StepOutcome> AnalyzeMailsAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);
        if (services.GetService<IWinoIntelligenceCoordinator>() is not { } coordinator)
            return Failed(step, Translator.VoiceResult_AnalysisUnavailable);

        var analyzed = new List<MailCopy>();
        string? error = null;
        foreach (var mail in mails.Take(MaxAnalyzedMails))
        {
            if (mail.AssignedAccount is not { } account
                || await CreateIntelligenceContextAsync(mail, account, ct).ConfigureAwait(false) is not { } context)
                continue;

            try
            {
                await coordinator.RequestProcessingAsync(context, ct).ConfigureAwait(false);
                analyzed.Add(mail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Wino Intelligence could not analyze a mail for an assistant step.");
                error ??= WinoAccountApiErrorTranslator.Translate(ex.Message);
            }
        }

        return analyzed.Count == 0
            ? Failed(step, error ?? Translator.VoiceResult_AnalysisUnavailable)
            : Done(step, Refs(analyzed), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Analyzed, CountText(analyzed.Count)));
    }

    private async Task<StepOutcome> CopyCodeAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        foreach (var mail in mails.Take(MaxCodeMails))
        {
            if (await ReadMimeAsync(mail, ct).ConfigureAwait(false) is not { } message)
                continue;

            var code = VerificationCodeExtractor.TryExtract(message.TextBody)
                ?? VerificationCodeExtractor.TryExtract(VerificationCodeExtractor.ToPlainText(message.HtmlBody))
                ?? VerificationCodeExtractor.TryExtract(message.Subject);
            if (code is null)
                continue;

            if (services.GetService<IClipboardService>() is not { } clipboard)
                return Failed(step, Translator.VoiceResult_ClipboardUnavailable);

            var copied = await clipboard.CopyTextAsync(code, ct).ConfigureAwait(false);
            return copied.IsSuccess
                ? Done(step, [Ref(mail)], string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_CodeCopied, code, Subject(mail)))
                : Failed(step, copied.ErrorMessage ?? Translator.VoiceResult_ClipboardUnavailable);
        }

        return Failed(step, Translator.VoiceResult_NoCode);
    }

    /// <summary>
    /// Unsubscribes headlessly where the sender offers a one-click unsubscribe (List-Unsubscribe-Post). Links that need
    /// a browser or a mail are skipped without asking, so a step that finds none ends with a note, not a failure.
    /// </summary>
    private async Task<StepOutcome> UnsubscribeAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.Count == 0)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        var service = services.GetService<IUnsubscriptionService>();
        var unsubscribed = new List<MailCopy>();
        foreach (var mail in mails.DistinctBy(static mail => mail.FromAddress?.Trim().ToLowerInvariant()))
        {
            if (service is null || await ReadMimeInformationAsync(mail, ct).ConfigureAwait(false) is not { MimeMessage: { } message } mime)
                continue;

            var info = mimeFileService.GetMailRenderModel(message, mime.Path ?? string.Empty)?.UnsubscribeInfo;
            if (info is not { IsOneClick: true, HttpLink: { } link } || !Uri.IsWellFormedUriString(link, UriKind.Absolute))
                continue;

            try
            {
                if (await service.OneClickUnsubscribeAsync(info).ConfigureAwait(false))
                    unsubscribed.Add(mail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "A one-click unsubscribe failed for an assistant step.");
            }
        }

        if (unsubscribed.Count > 0)
        {
            var senders = unsubscribed.Select(static mail => Plain(SenderName(mail))).Distinct(StringComparer.CurrentCultureIgnoreCase);
            return Done(step, Refs(unsubscribed), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Unsubscribed, string.Join(", ", senders)));
        }

        var oneSender = mails.Select(static mail => mail.FromAddress).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return Done(step, Refs(mails), oneSender
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoUnsubscribeOne, Plain(SenderName(mails[0])))
            : Translator.VoiceResult_NoUnsubscribe);
    }

    // ---- drafts --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends drafts the way the composer's Send does: only existing drafts the target names. A draft that is not on the
    /// server yet, has no recipients or whose account has no sending alias or Sent folder is left alone.
    /// </summary>
    private async Task<StepOutcome> SendDraftsAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var drafts = (await ResolveMailsAsync(step, run, ct).ConfigureAwait(false))
            .Where(static mail => mail.IsDraft && !mail.IsLocalDraft)
            .ToList();
        if (drafts.Count == 0)
            return Failed(step, Translator.VoiceResult_NoDraftsToSend);

        var sent = new List<MailCopy>();
        foreach (var draft in drafts)
        {
            if (draft.AssignedAccount is not { } account || draft.AssignedFolder is not { } draftFolder)
                continue;

            var message = await ReadMimeAsync(draft, ct).ConfigureAwait(false);
            if (message is null || message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
                continue;

            var alias = await ResolveSendingAliasAsync(account.Id, message).ConfigureAwait(false);
            var sentFolder = await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Sent).ConfigureAwait(false);
            if (alias is null || sentFolder is null)
                continue;

            await requestDelegator.ExecuteAsync(new SendDraftPreparationRequest(draft, alias, sentFolder, draftFolder, account.Preferences, message.GetBase64MimeMessage())).ConfigureAwait(false);
            sent.Add(draft);
        }

        if (sent.Count == 0)
            return Failed(step, Translator.VoiceResult_NoDraftsToSend);

        var line = string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_DraftsSent, DraftsText(sent.Count));
        return Done(step, Refs(sent), sent.Count < drafts.Count
            ? $"{line} · {string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_DraftsSkipped, drafts.Count - sent.Count)}"
            : line);
    }

    /// <summary>The alias in the draft's From header, else the account's primary one.</summary>
    private async Task<MailAccountAlias?> ResolveSendingAliasAsync(Guid accountId, MimeMessage message)
    {
        var from = message.From.Mailboxes.FirstOrDefault()?.Address;
        if (!string.IsNullOrWhiteSpace(from))
        {
            var aliases = await accountService.GetAccountAliasesAsync(accountId).ConfigureAwait(false);
            if (aliases?.FirstOrDefault(alias => string.Equals(alias.AliasAddress, from, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match;
        }

        return await accountService.GetPrimaryAccountAliasAsync(accountId).ConfigureAwait(false);
    }

    /// <summary>A new draft from resolved recipients, subject and body, opened in the composer. Never sent.</summary>
    private async Task<StepOutcome> CreateDraftAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (step.Args is not StepArgs.DraftArgs draft)
            return Failed(step, Translator.VoiceIssue_MissingDetails);
        if (Host is not { } host)
            return Failed(step, Translator.VoiceResult_LaunchUnavailable);

        var accounts = await AccountsAsync(run).ConfigureAwait(false);
        var account = await AccountAsync(run, draft.AccountId).ConfigureAwait(false) ?? (accounts.Count == 1 ? accounts[0] : null);
        if (account is null)
            return Failed(step, Translator.VoiceIssue_AccountUnnamed);

        var to = draft.To.ToList();
        if (to.Count == 0 && step.Target is StepTarget.FromStep from)
            to = await PeopleOfStepAsync(run, from.N, from.Part, ct).ConfigureAwait(false);

        // MailToUri reads RFC 6068 text; the body is inserted into the draft's HTML as is, so it is encoded first.
        var parameters = new List<string>();
        if (draft.Cc.Count > 0)
            parameters.Add("cc=" + Uri.EscapeDataString(string.Join(",", draft.Cc.Select(static person => person.Address))));
        if (draft.Bcc.Count > 0)
            parameters.Add("bcc=" + Uri.EscapeDataString(string.Join(",", draft.Bcc.Select(static person => person.Address))));
        if (!string.IsNullOrWhiteSpace(draft.Subject))
            parameters.Add("subject=" + Uri.EscapeDataString(draft.Subject.Trim()));
        if (draft.Body is { Text.Length: > 0 } body)
            parameters.Add("body=" + Uri.EscapeDataString(WebUtility.HtmlEncode(body.Text.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>")));

        var mailTo = "mailto:" + string.Join(",", to.Select(static person => Uri.EscapeDataString(person.Address))) + (parameters.Count > 0 ? "?" + string.Join("&", parameters) : string.Empty);
        var options = new DraftCreationOptions
        {
            Reason = DraftCreationReason.Empty,
            MailToUri = new MailToUri(mailTo)
        };

        var created = await CreateAndOpenDraftAsync(host, account, options, null, message =>
        {
            // The display names of resolved recipients are not part of a mailto; they are written into the message itself.
            foreach (var (list, people) in new[] { (message.To, to), (message.Cc, draft.Cc.ToList()), (message.Bcc, draft.Bcc.ToList()) })
            {
                foreach (var person in people.Where(static person => !string.IsNullOrWhiteSpace(person.Name)))
                {
                    var index = list.Mailboxes.ToList().FindIndex(mailbox => SameAddress(mailbox.Address, person.Address));
                    if (index >= 0)
                        list[index] = new MailboxAddress(person.Name, person.Address);
                }
            }
        }).ConfigureAwait(false);

        run.Mails[step.N] = [created];
        return Done(step, [new ItemRef(ItemKind.Draft, account.Id, created.UniqueId.ToString())], to.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_DraftOpened, string.Join(", ", to.Select(static person => Plain(person.Name ?? person.Address))))
            : Translator.VoiceResult_EmptyDraftOpened);
    }

    /// <summary>A reply, reply-all or forward of the first target mail, opened in the composer. Never sent.</summary>
    private async Task<StepOutcome> CreateReplyDraftAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (Host is not { } host)
            return Failed(step, Translator.VoiceResult_LaunchUnavailable);

        var mails = await ResolveMailsAsync(step, run, ct).ConfigureAwait(false);
        if (mails.FirstOrDefault() is not { AssignedAccount: { } account } mail)
            return NothingMatched(step, Translator.VoiceIssue_NoMails);

        var mime = await ReadMimeInformationAsync(mail, ct).ConfigureAwait(false);
        if (mime?.MimeMessage is null)
            return Failed(step, Translator.VoiceResult_MailNotDownloaded);

        var draft = step.Args as StepArgs.DraftArgs;
        var kind = draft?.Kind ?? ReplyKind.Reply;
        var options = new DraftCreationOptions
        {
            Reason = kind switch
            {
                ReplyKind.ReplyAll => DraftCreationReason.ReplyAll,
                ReplyKind.Forward => DraftCreationReason.Forward,
                _ => DraftCreationReason.Reply
            },
            InitialBodyText = draft?.Body?.Text ?? string.Empty,
            ReferencedMessage = new ReferencedMessage { MimeMessage = mime.MimeMessage, MailCopy = mail }
        };

        var subject = Subject(mail);
        if (kind == ReplyKind.Forward)
        {
            // A forward starts without recipients, so the ones the user named are written into the new draft.
            var to = draft?.To ?? [];
            var cc = draft?.Cc ?? [];
            var forwarded = await CreateAndOpenDraftAsync(host, account, options, mail, message =>
            {
                message.To.AddRange(to.Select(static person => new MailboxAddress(person.Name ?? person.Address, person.Address)));
                message.Cc.AddRange(cc.Select(static person => new MailboxAddress(person.Name ?? person.Address, person.Address)));
            }).ConfigureAwait(false);

            run.Mails[step.N] = [forwarded];
            return Done(step, [new ItemRef(ItemKind.Draft, account.Id, forwarded.UniqueId.ToString())], to.Count > 0
                ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ForwardOpened, subject, string.Join(", ", to.Select(static person => Plain(person.Name ?? person.Address))))
                : string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_ForwardDraftOpened, subject));
        }

        var reply = await CreateAndOpenDraftAsync(host, account, options, mail).ConfigureAwait(false);
        run.Mails[step.N] = [reply];
        return Done(step, [new ItemRef(ItemKind.Draft, account.Id, reply.UniqueId.ToString())],
            string.Format(CultureInfo.CurrentCulture, kind == ReplyKind.ReplyAll ? Translator.VoiceResult_ReplyAllOpened : Translator.VoiceResult_ReplyOpened, Plain(SenderName(mail))));
    }

    /// <summary>
    /// Creates the draft and queues it the way the composer's own new-mail and reply commands do, then lets the platform
    /// open it. <paramref name="referenceMail"/> is the mail a reply answers; <paramref name="adjust"/> changes the new
    /// draft's message before it is saved and queued. Returns the draft's stored copy.
    /// </summary>
    private async Task<MailCopy> CreateAndOpenDraftAsync(IAgentPlanHost host, MailAccount account, DraftCreationOptions options, MailCopy? referenceMail, Action<MimeMessage>? adjust = null)
    {
        if (await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft).ConfigureAwait(false) is null)
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSpecialFolder, AccountTitle(account), Translator.VoiceStep_Folder_Drafts));

        var (draftMailCopy, draftBase64MimeMessage) = await mailService.CreateDraftAsync(account.Id, options).ConfigureAwait(false);
        if (adjust is not null)
        {
            var message = draftBase64MimeMessage.GetMimeMessageFromBase64();
            adjust(message);
            await mimeFileService.SaveMimeMessageAsync(draftMailCopy.FileId, message, account.Id).ConfigureAwait(false);
            draftBase64MimeMessage = message.GetBase64MimeMessage();
        }

        var preparation = referenceMail is null
            ? new DraftPreparationRequest(account, draftMailCopy, draftBase64MimeMessage, options.Reason)
            : new DraftPreparationRequest(account, draftMailCopy, draftBase64MimeMessage, options.Reason, referenceMail);
        await requestDelegator.ExecuteAsync(preparation).ConfigureAwait(false);

        draftMailCopy.AssignedAccount ??= account;
        await host.OpenDraftAsync(account, draftMailCopy).ConfigureAwait(false);
        return draftMailCopy;
    }

    // ---- system --------------------------------------------------------------------------------------------------

    /// <summary>Undoes the latest queued action that can still be undone in each account the step names.</summary>
    private async Task<StepOutcome> UndoAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        if (services.GetService<ISynchronizationManager>() is not { } manager)
            return Failed(step, Translator.VoiceResult_NothingToUndo);

        var undone = 0;
        foreach (var account in await StepAccountsAsync(step, run).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await manager.UndoLatestQueuedAction(account.Id).ConfigureAwait(false))
                    undone++;
            }
            catch (InvalidOperationException ex)
            {
                Log.Debug(ex, "Undo is not available for an assistant step.");
            }
        }

        return undone == 0
            ? Failed(step, Translator.VoiceResult_NothingToUndo)
            : Done(step, ItemRefsOf(step.Target), Translator.VoiceResult_Undone);
    }

    private async Task<StepOutcome> SyncMailAsync(ResolvedStep step, PlanRun run, CancellationToken ct)
    {
        var accounts = await StepAccountsAsync(step, run).ConfigureAwait(false);
        if (accounts.Count == 0)
            return Failed(step, Translator.VoiceIssue_AccountUnnamed);

        foreach (var account in accounts)
        {
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }));
        }

        return Done(step, accounts.Select(static account => ItemRef.Account(account.Id)), string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_SyncStarted, Plain(AccountsLabel(accounts))));
    }

    private async Task<WinoIntelligenceContext?> CreateIntelligenceContextAsync(MailCopy mail, MailAccount account, CancellationToken ct)
    {
        var mime = await ReadMimeInformationAsync(mail, ct).ConfigureAwait(false);
        var html = mime?.MimeMessage?.HtmlBody;
        if (string.IsNullOrWhiteSpace(html) && mime?.MimeMessage?.TextBody is { } text)
            html = WebUtility.HtmlEncode(text);
        if (string.IsNullOrWhiteSpace(html))
            return null;

        return new WinoIntelligenceContext(
            $"{account.Id:N}:{mail.UniqueId:N}",
            account.Id,
            mail.UniqueId,
            mail.FileId,
            mail.Id,
            account.Address,
            account.ProviderType,
            account.Preferences?.IsSemanticIndexingEnabled == true,
            mail.Subject ?? string.Empty,
            SenderName(mail),
            new DateTimeOffset(DateTime.SpecifyKind(mail.CreationDate, DateTimeKind.Utc)),
            html,
            IntelligenceMetadata: mail.IntelligenceMetadata);
    }
}
