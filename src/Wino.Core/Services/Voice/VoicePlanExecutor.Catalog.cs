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
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Misc;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Models.Voice;
using Wino.Core.Requests.Mail;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Messaging.Server;
using Wino.Services.Voice;

namespace Wino.Core.Services.Voice;

/// <summary>
/// The mail actions of the full catalog (move, junk, Focused, pin, categories, send drafts, translate, analyze, codes,
/// unsubscribe, undo and sync) and the <c>@result</c> people a command takes from an earlier one. Each runs through the
/// same request or service the app's own command uses.
/// </summary>
public sealed partial class VoicePlanExecutor
{
    private const int MaxAnalyzedMails = 25;
    private const int MaxRecipientMails = 20;
    private const int MaxCodeMails = 5;

    /// <summary>
    /// Replaces the <c>@result.N</c> tokens of a command with the people of command N's result: the contacts it found,
    /// the senders or recipients of its mails, the attendees or organizer of its events. <paramref name="unresolved"/> is
    /// set when a token that narrows a search (a sender, an attendee) found nobody, so the step must not run broader.
    /// </summary>
    private async Task<(VoicePlanPreviewStep Step, bool Unresolved)> ExpandResultTokensAsync(VoicePlanPreviewStep step, ResolveState state, CancellationToken cancellationToken)
    {
        var command = step.Command;
        if (!HasResultTokens(command))
            return (step, false);

        var unresolved = false;

        async Task<List<string>> ExpandListAsync(IEnumerable<string>? values)
        {
            var result = new List<string>();
            foreach (var value in values ?? [])
            {
                if (IsPeopleToken(value))
                    result.AddRange(await PeopleOfTokenAsync(value, state, cancellationToken).ConfigureAwait(false));
                else if (!string.IsNullOrWhiteSpace(value))
                    result.Add(value);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        async Task<string?> ExpandOneAsync(string? value)
        {
            if (!IsPeopleToken(value))
                return value;

            var people = await PeopleOfTokenAsync(value, state, cancellationToken).ConfigureAwait(false);
            if (people.Count == 0)
                unresolved = true;
            return people.FirstOrDefault();
        }

        var expanded = command;
        if (command.Draft is { } draft)
            expanded = expanded with { Draft = draft with { To = await ExpandListAsync(draft.To).ConfigureAwait(false), Cc = await ExpandListAsync(draft.Cc).ConfigureAwait(false) } };
        if (command.Event is { } details)
            expanded = expanded with { Event = details with { Attendees = await ExpandListAsync(details.Attendees).ConfigureAwait(false) } };
        if (command.Mail is { } mail && IsPeopleToken(mail.Sender))
            expanded = expanded with { Mail = mail with { Sender = await ExpandOneAsync(mail.Sender).ConfigureAwait(false) } };
        if (command.Events is { } events && IsPeopleToken(events.Attendee))
            expanded = expanded with { Events = events with { Attendee = await ExpandOneAsync(events.Attendee).ConfigureAwait(false) } };

        var (text, detail) = VoicePlanPreviewBuilder.DescribeCommand(expanded);
        return (step with { Command = expanded, MarkedText = text, Detail = detail }, unresolved);
    }

    private static bool HasResultTokens(VoiceCommand command)
        => (command.Draft?.To ?? []).Concat(command.Draft?.Cc ?? []).Concat(command.Event?.Attendees ?? [])
            .Append(command.Mail?.Sender).Append(command.Events?.Attendee)
            .Any(IsPeopleToken);

    private static bool IsPeopleToken(string? value)
        => VoiceResultTokens.IsToken(value) || (value is not null && value.StartsWith(VoiceContextTokens.Prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The people a token names; an unknown token names nobody.</summary>
    private async Task<List<string>> PeopleOfTokenAsync(string? token, ResolveState state, CancellationToken cancellationToken)
    {
        if (VoiceResultTokens.TryParse(token, out var index, out var part))
            return await PeopleOfResultAsync(index - 1, part, state, cancellationToken).ConfigureAwait(false);
        if (VoiceContextTokens.TryParse(token, out var contextPart))
            return await PeopleOfSelectionAsync(contextPart, state, cancellationToken).ConfigureAwait(false);
        return [];
    }

    /// <summary>The addresses of the people command <paramref name="index"/> (0-based) found, never the user's own.</summary>
    private async Task<List<string>> PeopleOfResultAsync(int index, string part, ResolveState state, CancellationToken cancellationToken)
    {
        var addresses = new List<string>();
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (state.ContactTargets.TryGetValue(index, out var contacts))
        {
            addresses.AddRange(contacts.Select(static target => target.Contact.PrimaryEmailAddress).OfType<string>());
        }
        else if (state.EventTargets.TryGetValue(index, out var events))
        {
            var attendeesById = new Dictionary<Guid, List<Wino.Core.Domain.Entities.Calendar.CalendarEventAttendee>>();
            foreach (var target in events)
            {
                own.Add(target.Account.Address ?? string.Empty);
                if (part != "organizer")
                {
                    var attendees = await GetAttendeesAsync(target.Event, attendeesById).ConfigureAwait(false);
                    addresses.AddRange(attendees.Select(static attendee => attendee.Email).OfType<string>());
                }

                if (part is "" or "organizer" && !string.IsNullOrWhiteSpace(target.Event.OrganizerEmail))
                    addresses.Add(target.Event.OrganizerEmail);
            }
        }
        else if (state.MailTargets.TryGetValue(index, out var targets))
        {
            foreach (var target in targets)
                own.Add(target.Account.Address ?? string.Empty);

            if (!state.Mails.TryGetValue(index, out var mails))
            {
                mails = [];
                foreach (var target in targets)
                    mails.AddRange(await FetchAsync(target, cancellationToken).ConfigureAwait(false));
                state.Mails[index] = mails;
            }

            if (part == "recipients")
            {
                foreach (var mail in mails.Take(MaxRecipientMails))
                {
                    if (await ReadMimeAsync(mail, cancellationToken).ConfigureAwait(false) is not { } message)
                        continue;

                    addresses.AddRange(message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(static mailbox => mailbox.Address));
                }
            }
            else
            {
                addresses.AddRange(mails.Select(static mail => mail.FromAddress));
            }
        }

        return addresses
            .Where(address => !string.IsNullOrWhiteSpace(address) && address.Contains('@') && !own.Contains(address.Trim()))
            .Select(static address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<MimeMessage?> ReadMimeAsync(MailCopy mail, CancellationToken cancellationToken)
        => (await ReadMimeInformationAsync(mail, cancellationToken).ConfigureAwait(false))?.MimeMessage;

    /// <summary>The stored MIME of a mail, or null when it isn't downloaded or can't be read.</summary>
    private async Task<MimeMessageInformation?> ReadMimeInformationAsync(MailCopy mail, CancellationToken cancellationToken)
    {
        if (mail.AssignedAccount is not { } account)
            return null;

        try
        {
            return await mimeFileService.GetMimeMessageInformationAsync(mail.FileId, account.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug(ex, "Could not read a mail's MIME for a voice step.");
            return null;
        }
    }

    private static StepOutcome ResolveCreateContactStep(VoicePlanPreviewStep step)
    {
        var change = step.Command.ContactChange;
        return string.IsNullOrWhiteSpace(change?.Name) && string.IsNullOrWhiteSpace(change?.Email) && string.IsNullOrWhiteSpace(change?.Phone)
            ? StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Contact))
            : StepOutcome.Keep(step.Detail);
    }

    /// <summary>
    /// What a mail action needs beyond its mails, in each account they are in: the folder a move puts them into, the
    /// category, an Outlook account for Focused and Other. Accounts without it are left out; a step left with none is dropped.
    /// </summary>
    private async Task<StepOutcome> ResolveMailChangeAsync(VoicePlanPreviewStep step, ResolveState state, StepOutcome outcome)
    {
        var command = step.Command;
        var index = step.CommandIndex;
        if (command.Action is not (VoiceAction.MoveToFolder or VoiceAction.Categorize or VoiceAction.Uncategorize or VoiceAction.MoveToFocused or VoiceAction.MoveToOther))
            return outcome;
        if (!state.MailTargets.TryGetValue(index, out var targets) || targets.Count == 0)
            return outcome;

        var change = command.MailChange;
        var kept = new List<VoiceMailTarget>();
        string? problem = null;

        switch (command.Action)
        {
            case VoiceAction.MoveToFolder:
            {
                if (change is null || (string.IsNullOrWhiteSpace(change.FolderId) && string.IsNullOrWhiteSpace(change.FolderName) && ToSpecialFolderType(change.FolderSpecialType) is null))
                    return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Folder));

                var destinations = new List<VoiceFolderTarget>();
                foreach (var target in targets)
                {
                    var (folder, folderProblem) = await ResolveSingleFolderAsync(target.Account, change.FolderId, change.FolderName, change.FolderSpecialType).ConfigureAwait(false);
                    if (folder is not { IsMoveTarget: true })
                    {
                        problem ??= folderProblem;
                        continue;
                    }

                    destinations.Add(new VoiceFolderTarget(folder, target.Account));
                    kept.Add(target);
                }

                state.FolderTargets[index] = destinations;
                break;
            }

            case VoiceAction.Categorize or VoiceAction.Uncategorize:
            {
                if (string.IsNullOrWhiteSpace(change?.CategoryName))
                    return StepOutcome.Drop(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Category));

                var name = change.CategoryName.Trim();
                var categories = new List<MailCategory>();
                foreach (var target in targets)
                {
                    var all = await mailCategoryService.GetCategoriesAsync(target.Account.Id).ConfigureAwait(false) ?? [];
                    var category = all.FirstOrDefault(category => SameText(category.Name, name)) ?? all.FirstOrDefault(category => Fits(category.Name, name));
                    if (category is null)
                        continue;

                    categories.Add(category);
                    kept.Add(target);
                }

                problem = string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoCategory, name);
                state.CategoryTargets[index] = categories;
                break;
            }

            default:
                kept.AddRange(targets.Where(static target => target.Account.ProviderType == MailProviderType.Outlook));
                problem = Translator.VoiceIssue_FocusedOutlookOnly;
                break;
        }

        if (kept.Count == 0)
            return StepOutcome.Drop(problem ?? string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoMails, PlainText(step)));
        if (kept.Count == targets.Count)
            return outcome;

        var total = kept.Sum(static target => target.Count);
        state.MailTargets[index] = kept;
        state.Counts[index] = total;
        return StepOutcome.Keep(WithCount(total, step.Detail));
    }

    /// <summary>One folder of an account, by provider id, well-known kind or name (exact first, then contained).</summary>
    private async Task<(MailItemFolder? Folder, string? Problem)> ResolveSingleFolderAsync(MailAccount account, string? folderId, string? folderName, SpecialFolderKind? special)
    {
        var all = await folderService.GetFoldersAsync(account.Id).ConfigureAwait(false) ?? [];
        var accountName = AccountTitle(account);

        if (!string.IsNullOrWhiteSpace(folderId) && all.FirstOrDefault(folder => string.Equals(folder.RemoteFolderId, folderId, StringComparison.Ordinal)) is { } byId)
            return (byId, null);

        if (ToSpecialFolderType(special) is { } type)
        {
            return all.FirstOrDefault(folder => folder.SpecialFolderType == type) is { } byType
                ? (byType, null)
                : (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoSpecialFolder, accountName, SpecialFolderName(special)));
        }

        if (string.IsNullOrWhiteSpace(folderName))
            return (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolder, folderId ?? string.Empty, accountName));

        var name = folderName.Trim();
        var match = all.FirstOrDefault(folder => SameName(folder, name, exact: true))
            ?? all.Where(folder => SameName(folder, name, exact: false)).OrderBy(static folder => folder.FolderName?.Length ?? 0).FirstOrDefault();
        return match is null
            ? (null, string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_NoFolder, name, accountName))
            : (match, null);
    }

    private static bool SameText(string? left, string right)
        => !string.IsNullOrWhiteSpace(left)
           && string.Compare(left.Trim(), right.Trim(), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    /// <summary>The second confirmation for the mail actions of the catalog that can't be taken back.</summary>
    private static bool AddMailCatalogRisks(VoicePlanResolution resolution, VoicePlanPreviewStep step, List<VoicePlanRisk> risks)
    {
        var command = step.Command;
        var isAlways = command.Action is VoiceAction.MoveToFocused or VoiceAction.MoveToOther && command.MailChange?.Always == true;
        if (command.Action != VoiceAction.SendDrafts && !isAlways)
            return false;

        var index = command.UsesResultOf is int source ? source - 1 : step.CommandIndex;
        if (!resolution.MailTargets.TryGetValue(index, out var targets) || targets.Count == 0)
            return true;

        var count = targets.Sum(static target => target.Count);
        var accounts = AccountsLabel(targets.Select(static target => target.Account).DistinctBy(static account => account.Id).ToList());
        if (command.Action == VoiceAction.SendDrafts)
        {
            var drafts = DraftsText(count);
            risks.Add(new VoicePlanRisk(step.CommandIndex,
                string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_SendDrafts, drafts, accounts),
                false,
                string.Format(CultureInfo.CurrentCulture, Translator.VoiceGuard_SendButton, drafts)));
            return true;
        }

        risks.Add(new VoicePlanRisk(step.CommandIndex,
            string.Format(CultureInfo.CurrentCulture,
                command.Action == VoiceAction.MoveToFocused ? Translator.VoiceGuard_AlwaysFocused : Translator.VoiceGuard_AlwaysOther,
                CountText(count)),
            false,
            Translator.VoiceGuard_Continue));
        return true;
    }

    private async Task<StepResult> MoveToFolderAsync(StepContext context, CancellationToken cancellationToken)
    {
        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);
        if (!context.Resolution.FolderTargets.TryGetValue(context.Step.CommandIndex, out var destinations) || destinations.Count == 0)
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Folder));

        var moved = 0;
        string? folderName = null;
        foreach (var accountMails in mails.Where(static mail => mail.AssignedAccount is not null).GroupBy(static mail => mail.AssignedAccount.Id))
        {
            if (destinations.FirstOrDefault(destination => destination.Account.Id == accountMails.Key)?.Folder is not { } folder)
                continue;

            var moving = accountMails.Where(mail => mail.FolderId != folder.Id).ToList();
            if (moving.Count == 0)
                continue;

            if (await QueueMailOperationAsync(MailOperation.Move, moving, folder).ConfigureAwait(false) is { } error)
                return StepResult.Failed(error);

            moved += moving.Count;
            folderName ??= folder.ResolvedDisplayName;
        }

        return moved == 0
            ? StepResult.Failed(Translator.VoiceResult_NoMailsLeft)
            : StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Moved, CountText(moved), folderName));
    }

    private async Task<StepResult> MoveToInboxTabAsync(StepContext context, bool focused, CancellationToken cancellationToken)
    {
        var mails = (await GetMailsAsync(context, cancellationToken).ConfigureAwait(false))
            .Where(static mail => mail.AssignedAccount?.ProviderType == MailProviderType.Outlook)
            .ToList();
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceIssue_FocusedOutlookOnly);

        if (context.Step.Command.MailChange?.Always == true)
        {
            // The rule is per sender, so one mail of each sender is enough to set it.
            var bySender = mails.DistinctBy(static mail => mail.FromAddress?.Trim().ToLowerInvariant()).ToList();
            if (await QueueMailOperationAsync(focused ? MailOperation.AlwaysMoveToFocused : MailOperation.AlwaysMoveToOther, bySender, null).ConfigureAwait(false) is { } alwaysError)
                return StepResult.Failed(alwaysError);

            var senders = string.Join(", ", bySender.Select(static mail => string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : mail.FromName).Take(3));
            return StepResult.Done(string.Format(CultureInfo.CurrentCulture, focused ? Translator.VoiceResult_AlwaysFocused : Translator.VoiceResult_AlwaysOther, senders));
        }

        if (await QueueMailOperationAsync(focused ? MailOperation.MoveToFocused : MailOperation.MoveToOther, mails, null).ConfigureAwait(false) is { } error)
            return StepResult.Failed(error);

        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, focused ? Translator.VoiceResult_MovedToFocused : Translator.VoiceResult_MovedToOther, CountText(mails.Count)));
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

    private async Task<StepResult> PinMailsAsync(StepContext context, bool pin, CancellationToken cancellationToken)
    {
        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        await mailService.ChangePinnedStatusAsync(mails.Select(static mail => mail.UniqueId).Distinct().ToList(), pin).ConfigureAwait(false);
        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, pin ? Translator.VoiceResult_Pinned : Translator.VoiceResult_Unpinned, CountText(mails.Count)));
    }

    /// <summary>
    /// Assigns or removes a category like the mail list does: stored locally first, then sent to Outlook, the only
    /// provider whose categories live on the server.
    /// </summary>
    private async Task<StepResult> CategorizeMailsAsync(StepContext context, bool assign, CancellationToken cancellationToken)
    {
        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);
        if (!context.Resolution.CategoryTargets.TryGetValue(context.Step.CommandIndex, out var categories) || categories.Count == 0)
            return StepResult.Failed(string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, Translator.VoiceIssue_Missing_Category));

        var changed = 0;
        string? categoryName = null;
        foreach (var accountMails in mails.Where(static mail => mail.AssignedAccount is not null).GroupBy(static mail => mail.AssignedAccount.Id))
        {
            if (categories.FirstOrDefault(category => category.MailAccountId == accountMails.Key) is not { } category)
                continue;

            var unique = accountMails.DistinctBy(static mail => mail.UniqueId).ToList();
            var ids = unique.Select(static mail => mail.UniqueId).ToList();
            if (assign)
                await mailCategoryService.AssignCategoryAsync(category.Id, ids).ConfigureAwait(false);
            else
                await mailCategoryService.UnassignCategoryAsync(category.Id, ids).ConfigureAwait(false);

            if (unique[0].AssignedAccount.ProviderType == MailProviderType.Outlook)
            {
                var requests = new List<IRequestBase>();
                foreach (var mail in unique)
                {
                    var names = await mailCategoryService.GetCategoryNamesForMailAsync(mail.UniqueId).ConfigureAwait(false) ?? [];
                    requests.Add(new MailCategoryAssignmentRequest(mail, category.Id, category.Name, names, assign));
                }

                await requestDelegator.ExecuteAsync(accountMails.Key, requests).ConfigureAwait(false);
            }

            changed += unique.Count;
            categoryName ??= category.Name;
        }

        if (changed == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, assign ? Translator.VoiceResult_Categorized : Translator.VoiceResult_Uncategorized, CountText(changed), categoryName));
    }

    /// <summary>
    /// Sends drafts the way the composer's Send does. A draft that isn't on the server yet, has no recipients or whose
    /// account has no sending alias or Sent folder is left alone.
    /// </summary>
    private async Task<StepResult> SendDraftsAsync(StepContext context, CancellationToken cancellationToken)
    {
        var drafts = (await GetMailsAsync(context, cancellationToken).ConfigureAwait(false))
            .Where(static mail => mail.IsDraft && !mail.IsLocalDraft)
            .ToList();
        if (drafts.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoDraftsToSend);

        var accountService = services.GetRequiredService<IAccountService>();
        var sent = 0;
        foreach (var draft in drafts)
        {
            if (draft.AssignedAccount is not { } account || draft.AssignedFolder is not { } draftFolder)
                continue;

            var message = await ReadMimeAsync(draft, cancellationToken).ConfigureAwait(false);
            if (message is null || message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
                continue;

            var alias = await ResolveSendingAliasAsync(accountService, account.Id, message).ConfigureAwait(false);
            var sentFolder = await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Sent).ConfigureAwait(false);
            if (alias is null || sentFolder is null)
                continue;

            await requestDelegator.ExecuteAsync(new SendDraftPreparationRequest(draft, alias, sentFolder, draftFolder, account.Preferences, message.GetBase64MimeMessage())).ConfigureAwait(false);
            sent++;
        }

        if (sent == 0)
            return StepResult.Failed(Translator.VoiceResult_NoDraftsToSend);

        var line = string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_DraftsSent, DraftsText(sent));
        return StepResult.Done(sent < drafts.Count
            ? $"{line} · {string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_DraftsSkipped, drafts.Count - sent)}"
            : line);
    }

    /// <summary>The alias in the draft's From header, else the account's primary one.</summary>
    private static async Task<MailAccountAlias?> ResolveSendingAliasAsync(IAccountService accountService, Guid accountId, MimeMessage message)
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

    /// <summary>
    /// Translates the first mail with Wino Intelligence and shows the translation with the result; the translation is
    /// also cached for the mail. The mail opens so the user can read the original next to it.
    /// </summary>
    private async Task<StepResult> TranslateAsync(StepContext context, CancellationToken cancellationToken)
    {
        var mail = (await GetMailsAsync(context, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        if (mail?.AssignedAccount is not { } account)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        if (services.GetService<IWinoIntelligenceCoordinator>() is not { } coordinator
            || await CreateIntelligenceContextAsync(mail, account, cancellationToken).ConfigureAwait(false) is not { } intelligenceContext)
            return StepResult.Failed(Translator.VoiceResult_TranslationUnavailable);

        var language = context.Step.Command.MailChange?.Language is { Length: > 0 } requested
            ? requested.Trim()
            : preferencesService.AiDefaultTranslationLanguageCode;
        var result = await coordinator.TranslateAsync(intelligenceContext, Guid.NewGuid(), null, language, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
            return StepResult.Failed(result.Error ?? Translator.VoiceResult_TranslationUnavailable);

        var text = string.Join(Environment.NewLine, result.Value.Translations.Select(static segment => segment.Text).Where(static segment => !string.IsNullOrWhiteSpace(segment))).Trim();
        await context.Host.OpenMailAsync(mail).ConfigureAwait(false);
        return new StepResult(
            string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Translated, Subject(mail), VoicePlanPreviewBuilder.LanguageName(language)),
            null,
            text.Length > 0 ? text : null);
    }

    private async Task<StepResult> AnalyzeMailsAsync(StepContext context, CancellationToken cancellationToken)
    {
        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);
        if (services.GetService<IWinoIntelligenceCoordinator>() is not { } coordinator)
            return StepResult.Failed(Translator.VoiceResult_AnalysisUnavailable);

        var analyzed = 0;
        string? error = null;
        foreach (var mail in mails.Take(MaxAnalyzedMails))
        {
            if (mail.AssignedAccount is not { } account
                || await CreateIntelligenceContextAsync(mail, account, cancellationToken).ConfigureAwait(false) is not { } intelligenceContext)
                continue;

            try
            {
                await coordinator.RequestProcessingAsync(intelligenceContext, cancellationToken).ConfigureAwait(false);
                analyzed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Wino Intelligence could not analyze a mail for a voice step.");
                error ??= WinoAccountApiErrorTranslator.Translate(ex.Message);
            }
        }

        return analyzed == 0
            ? StepResult.Failed(error ?? Translator.VoiceResult_AnalysisUnavailable)
            : StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Analyzed, CountText(analyzed)));
    }

    private async Task<StepResult> CopyCodeAsync(StepContext context, CancellationToken cancellationToken)
    {
        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        foreach (var mail in mails.Take(MaxCodeMails))
        {
            if (await ReadMimeAsync(mail, cancellationToken).ConfigureAwait(false) is not { } message)
                continue;

            var code = VerificationCodeExtractor.TryExtract(message.TextBody)
                ?? VerificationCodeExtractor.TryExtract(VerificationCodeExtractor.ToPlainText(message.HtmlBody))
                ?? VerificationCodeExtractor.TryExtract(message.Subject);
            if (code is null)
                continue;

            if (services.GetService<IClipboardService>() is not { } clipboard)
                return StepResult.Failed(Translator.VoiceResult_ClipboardUnavailable);

            var copied = await clipboard.CopyTextAsync(code, cancellationToken).ConfigureAwait(false);
            return copied.IsSuccess
                ? StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_CodeCopied, code, Subject(mail)))
                : StepResult.Failed(copied.ErrorMessage ?? Translator.VoiceResult_ClipboardUnavailable);
        }

        return StepResult.Failed(Translator.VoiceResult_NoCode);
    }

    /// <summary>
    /// Unsubscribes headlessly where the sender offers a one-click unsubscribe (List-Unsubscribe-Post). Links that need
    /// a browser or a mail are skipped without asking, so a step that finds none ends with a note, not a failure.
    /// </summary>
    private async Task<StepResult> UnsubscribeAsync(StepContext context, CancellationToken cancellationToken)
    {
        var mails = await GetMailsAsync(context, cancellationToken).ConfigureAwait(false);
        if (mails.Count == 0)
            return StepResult.Failed(Translator.VoiceResult_NoMailsLeft);

        var service = services.GetService<IUnsubscriptionService>();
        var unsubscribed = new List<string>();
        foreach (var mail in mails.DistinctBy(static mail => mail.FromAddress?.Trim().ToLowerInvariant()))
        {
            if (service is null || await ReadMimeInformationAsync(mail, cancellationToken).ConfigureAwait(false) is not { MimeMessage: { } message } mime)
                continue;

            var info = mimeFileService.GetMailRenderModel(message, mime.Path ?? string.Empty)?.UnsubscribeInfo;
            if (info is not { IsOneClick: true, HttpLink: { } link } || !Uri.IsWellFormedUriString(link, UriKind.Absolute))
                continue;

            try
            {
                if (await service.OneClickUnsubscribeAsync(info).ConfigureAwait(false))
                    unsubscribed.Add(string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : mail.FromName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "A one-click unsubscribe failed for a voice step.");
            }
        }

        if (unsubscribed.Count > 0)
            return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_Unsubscribed, string.Join(", ", unsubscribed.Distinct(StringComparer.CurrentCultureIgnoreCase))));

        var sender = mails[0];
        return StepResult.Done(mails.Select(static mail => mail.FromAddress).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_NoUnsubscribeOne, string.IsNullOrWhiteSpace(sender.FromName) ? sender.FromAddress : sender.FromName)
            : Translator.VoiceResult_NoUnsubscribe);
    }

    private async Task<StepResult> UndoAsync(StepContext context)
    {
        if (services.GetService<ISynchronizationManager>() is not { } manager)
            return StepResult.Failed(Translator.VoiceResult_NothingToUndo);

        foreach (var account in StepAccounts(context.Group))
        {
            try
            {
                await manager.UndoLatestQueuedAction(account.Id).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Log.Debug(ex, "Undo is not available for a voice step.");
            }
        }

        return StepResult.Done(Translator.VoiceResult_Undone);
    }

    private static StepResult SyncMail(StepContext context)
    {
        var accounts = StepAccounts(context.Group);
        if (accounts.Count == 0)
            return StepResult.Failed(Translator.VoiceIssue_AccountUnnamed);

        foreach (var account in accounts)
        {
            WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.FullFolders
            }));
        }

        return StepResult.Done(string.Format(CultureInfo.CurrentCulture, Translator.VoiceResult_SyncStarted, AccountsLabel(accounts)));
    }

    private static List<MailAccount> StepAccounts(VoicePlanPreviewGroup group)
        => group.Account is { } account ? [account] : group.Candidates.ToList();

    private async Task<WinoIntelligenceContext?> CreateIntelligenceContextAsync(MailCopy mail, MailAccount account, CancellationToken cancellationToken)
    {
        var mime = await ReadMimeInformationAsync(mail, cancellationToken).ConfigureAwait(false);
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
            string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress ?? string.Empty : mail.FromName,
            new DateTimeOffset(DateTime.SpecifyKind(mail.CreationDate, DateTimeKind.Utc)),
            html,
            IntelligenceMetadata: mail.IntelligenceMetadata);
    }

    private static string DraftsText(int count)
        => count == 1 ? Translator.VoiceCount_Draft : string.Format(CultureInfo.CurrentCulture, Translator.VoiceCount_Drafts, count);
}
