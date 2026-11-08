#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Services.Voice;

public sealed class VoicePlanPreviewService(
    IAccountService accountService,
    ITaskService taskService,
    ICalendarService calendarService,
    IContactService contactService,
    IPreferencesService preferencesService) : IVoicePlanPreviewService
{
    public async Task<VoicePlanPreview> CreateAsync(VoicePlan plan)
    {
        List<MailAccount> accounts;
        try
        {
            accounts = await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the accounts for a voice plan.");
            accounts = [];
        }

        return VoicePlanPreviewBuilder.Build(plan, accounts, await GetCreationDefaultsAsync(plan, accounts).ConfigureAwait(false));
    }

    /// <summary>
    /// The accounts that new tasks and events go to when the user names none: the account of the last used task list,
    /// else the first account with a writable task list; the first account with a writable calendar.
    /// </summary>
    private async Task<VoiceCreationDefaults> GetCreationDefaultsAsync(VoicePlan plan, List<MailAccount> accounts)
    {
        var commands = plan.Commands ?? [];
        MailAccount? taskAccount = null;
        MailAccount? eventAccount = null;
        MailAccount? contactAccount = null;
        try
        {
            if (commands.Any(static command => command.Action is VoiceAction.CreateTask or VoiceAction.CreateTaskList))
            {
                var lists = (await taskService.GetTaskListsAsync().ConfigureAwait(false) ?? []).Where(static list => !list.IsReadOnly).ToList();
                var lastUsed = lists.FirstOrDefault(list => list.Id == preferencesService.LastUsedTaskListId);
                taskAccount = (lastUsed is null ? null : accounts.FirstOrDefault(account => account.Id == lastUsed.MailAccountId))
                    ?? accounts.FirstOrDefault(account => lists.Any(list => list.MailAccountId == account.Id));
            }

            if (commands.Any(static command => command.Action == VoiceAction.CreateEvent))
            {
                foreach (var account in accounts)
                {
                    var calendars = await calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
                    if (calendars.Any(static calendar => !calendar.IsReadOnly))
                    {
                        eventAccount = account;
                        break;
                    }
                }
            }

            if (commands.Any(static command => command.Action == VoiceAction.CreateContact))
            {
                var destinations = await contactService.GetCreateDestinationsAsync().ConfigureAwait(false) ?? [];
                var destination = VoiceContactDestinations.Pick(destinations, preferencesService, null);
                contactAccount = destination is null ? null : accounts.FirstOrDefault(account => account.Id == destination.MailAccountId);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not find the default accounts for new tasks and events.");
        }

        return new VoiceCreationDefaults(taskAccount, eventAccount, contactAccount);
    }
}

/// <summary>Where a new task, event or contact goes when the command names no account; null keeps the first account.</summary>
public sealed record VoiceCreationDefaults(MailAccount? TaskAccount, MailAccount? EventAccount, MailAccount? ContactAccount = null);

/// <summary>The address book a new contact goes to, chosen the way the contact editor chooses it.</summary>
public static class VoiceContactDestinations
{
    public static ContactCreateDestination? Pick(IEnumerable<ContactCreateDestination> destinations, IPreferencesService preferences, Guid? accountId)
    {
        var writable = destinations.Where(destination => !destination.IsReadOnly && (accountId is null || destination.MailAccountId == accountId)).ToList();
        var preferred = preferences.ContactCreationBehavior switch
        {
            NewItemDestinationBehavior.Specific => preferences.SpecificContactAddressBookId,
            NewItemDestinationBehavior.LastUsed => preferences.LastUsedContactAddressBookId,
            _ => null
        };

        return writable.FirstOrDefault(destination => destination.AddressBookId == preferred)
            ?? writable.OrderByDescending(static destination => destination.SourceKind != ContactSourceKind.Local)
                .ThenByDescending(static destination => destination.IsDefault)
                .FirstOrDefault();
    }
}

/// <summary>
/// The pure part of <see cref="VoicePlanPreviewService"/>: account matching and step text. The planner returns how
/// the user named an account as typed facts in any language; this compares them with the local accounts as data.
/// </summary>
public static class VoicePlanPreviewBuilder
{
    public static VoicePlanPreview Build(VoicePlan plan, IReadOnlyList<MailAccount> accounts, VoiceCreationDefaults? defaults = null)
    {
        var ordered = accounts.OrderBy(static account => account.Order).ThenBy(static account => account.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var commands = plan.Commands ?? [];
        var resolutions = new List<Resolution>(commands.Count);
        var groups = new List<GroupBuilder>();

        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            var resolution = command.UsesResultOf is int source && source >= 1 && source <= index
                ? resolutions[source - 1]
                : Resolve(command, ordered, defaults);
            resolutions.Add(resolution);

            var isIssue = command.Action == VoiceAction.NotActionable;

            // A part Wino can't do sits under the account it named; when it named none, it stands on its own.
            if (isIssue && resolution.Kind is not (VoicePlanAccountKind.Account or VoicePlanAccountKind.AllAccounts))
                resolution = Resolution.None;

            var group = groups.FirstOrDefault(candidate => candidate.Resolution.Key == resolution.Key);
            if (group is null)
            {
                group = new GroupBuilder(resolution);
                groups.Add(group);
            }

            if (isIssue)
            {
                group.Issues.Add(new VoicePlanPreviewIssue(DescribeIssue(command), command));
                continue;
            }

            var (text, detail) = DescribeStep(command);
            group.Steps.Add(new VoicePlanPreviewStep(group.Steps.Count + 1, text, detail, command, index));
        }

        return new VoicePlanPreview(groups.Select(ToGroup).ToList());
    }

    /// <summary>
    /// Steps for an account Wino could not tell can't run, so they are not offered: the group becomes one banner that
    /// says which account was not found, with no header and nothing to accept.
    /// </summary>
    private static VoicePlanPreviewGroup ToGroup(GroupBuilder group)
    {
        var resolution = group.Resolution;
        if (resolution.Kind != VoicePlanAccountKind.Unresolved)
        {
            return new VoicePlanPreviewGroup(resolution.Kind, resolution.Account, resolution.Candidates, resolution.Spoken, group.Steps, group.Issues);
        }

        var issues = new List<VoicePlanPreviewIssue>(group.Issues);
        if (group.Steps.Count > 0)
            issues.Insert(0, new VoicePlanPreviewIssue(DescribeUnresolvedAccount(resolution), group.Steps[0].Command));

        return new VoicePlanPreviewGroup(VoicePlanAccountKind.None, null, resolution.Candidates, resolution.Spoken, Array.Empty<VoicePlanPreviewStep>(), issues);
    }

    private static string DescribeUnresolvedAccount(Resolution resolution)
    {
        if (resolution.Candidates.Count > 1 && !string.IsNullOrWhiteSpace(resolution.Spoken))
        {
            var names = resolution.Candidates.Select(static account => string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name);
            return string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_AccountAmbiguous, resolution.Spoken, JoinChoices(names.ToList()));
        }

        return string.IsNullOrWhiteSpace(resolution.Spoken)
            ? Translator.VoiceIssue_AccountUnnamed
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_AccountNotFound, resolution.Spoken);
    }

    private static Resolution Resolve(VoiceCommand command, List<MailAccount> accounts, VoiceCreationDefaults? defaults)
    {
        if (command.Action == VoiceAction.CanceledByUser)
            return Resolution.None;

        if (accounts.Count == 0)
            return Resolution.Unresolved(Array.Empty<MailAccount>(), null);

        if (accounts.Count == 1)
            return Resolution.ForAccount(accounts[0]);

        var reference = command.Mailbox?.Account;
        var spoken = Describe(reference);

        // Without a named account these look everywhere: the device finds which account holds the task, contact or
        // folder, and undo and sync cover every account.
        if (spoken is null && command.Mailbox?.Scope != MailboxScope.All && SearchesEveryAccount(command))
            return new Resolution(VoicePlanAccountKind.AllAccounts, null, accounts, null, "all");

        if (command.Mailbox is not { } selection || selection.Scope == MailboxScope.Unresolved)
            return Resolution.Unresolved(accounts, null);

        if (selection.Scope == MailboxScope.All)
            return new Resolution(VoicePlanAccountKind.AllAccounts, null, accounts, null, "all");

        if (reference is null || spoken is null)
        {
            // A single mailbox without a name: the planner's default for creating things.
            var preferred = command.Action switch
            {
                VoiceAction.CreateTask or VoiceAction.CreateTaskList => defaults?.TaskAccount,
                VoiceAction.CreateEvent => defaults?.EventAccount,
                VoiceAction.CreateContact => defaults?.ContactAccount,
                _ => null
            };
            return Resolution.ForAccount(preferred ?? accounts[0]);
        }

        var matches = Match(reference, accounts);
        return matches.Count == 1 ? Resolution.ForAccount(matches[0]) : Resolution.Unresolved(matches, spoken);
    }

    private static bool SearchesEveryAccount(VoiceCommand command)
        => command.Action is VoiceAction.Undo or VoiceAction.SyncMail or VoiceAction.RenameTaskList or VoiceAction.DeleteTaskList
           || (command.UsesResultOf is null && (VoiceCommand.ActsOnTasks(command.Action) || VoiceCommand.ActsOnContacts(command.Action)))
           || (VoiceCommand.ActsOnFolder(command.Action) && command.Folder is { } folder
               && (!string.IsNullOrWhiteSpace(folder.FolderName) || !string.IsNullOrWhiteSpace(folder.FolderId)));

    public static List<MailAccount> MatchAccounts(AccountReference reference, IEnumerable<MailAccount> accounts)
        => Match(reference, accounts.ToList());

    private static List<MailAccount> Match(AccountReference reference, List<MailAccount> accounts)
    {
        IEnumerable<MailAccount> matches = accounts;

        if (reference.Provider is { } provider)
        {
            var providerType = provider switch
            {
                MailProviderKind.Outlook => MailProviderType.Outlook,
                MailProviderKind.Gmail => MailProviderType.Gmail,
                MailProviderKind.POP3 => MailProviderType.POP3,
                _ => MailProviderType.IMAP4
            };
            matches = matches.Where(account => account.ProviderType == providerType);
        }

        if (!string.IsNullOrWhiteSpace(reference.Address))
        {
            var address = reference.Address.Trim();
            matches = address.Contains('@')
                ? matches.Where(account => SameText(account.Address, address) || SameText(account.AuthenticationAddress, address))
                : matches.Where(account => IsInDomain(account.Address, address) || IsInDomain(account.AuthenticationAddress, address));
        }

        if (!string.IsNullOrWhiteSpace(reference.Name))
        {
            var name = reference.Name.Trim();
            var named = matches.ToList();
            var exact = named.Where(account => NameValues(account).Any(value => SameText(value, name))).ToList();
            matches = exact.Count > 0
                ? exact
                : named.Where(account => NameValues(account).Any(value => value.Contains(name, StringComparison.CurrentCultureIgnoreCase)));
        }

        var result = matches.ToList();
        if (reference.Position is int position)
        {
            // "My first account" counts in the account list, or among the accounts the other facts already matched.
            var pool = result.Count > 0 && result.Count < accounts.Count ? result : accounts;
            return position >= 1 && position <= pool.Count ? new List<MailAccount> { pool[position - 1] } : new List<MailAccount>();
        }

        return result;
    }

    private static IEnumerable<string> NameValues(MailAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.Name)) yield return account.Name;
        if (!string.IsNullOrWhiteSpace(account.SenderName)) yield return account.SenderName;
        if (!string.IsNullOrWhiteSpace(account.MergedInbox?.Name)) yield return account.MergedInbox.Name;
        if (account.Address?.IndexOf('@') is int at && at > 0) yield return account.Address[..at];
    }

    private static bool SameText(string? left, string right)
        => !string.IsNullOrWhiteSpace(left) && string.Equals(left.Trim(), right, StringComparison.CurrentCultureIgnoreCase);

    private static bool IsInDomain(string? address, string domain)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var at = address.LastIndexOf('@');
        if (at < 0)
            return false;

        var host = address[(at + 1)..];
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Describe(AccountReference? reference)
    {
        if (reference is null)
            return null;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(reference.Name)) parts.Add(reference.Name.Trim());
        if (!string.IsNullOrWhiteSpace(reference.Address)) parts.Add(reference.Address.Trim());
        if (reference.Provider is { } provider) parts.Add(provider.ToString());
        if (reference.Position is int position) parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_AccountPosition, position));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The step text and detail of a command, for a command the executor changed (its people filled in).</summary>
    public static (string Text, string? Detail) DescribeCommand(VoiceCommand command) => DescribeStep(command);

    public static string MinutesText(int minutes) => DescribeMinutes(minutes);

    private static (string Text, string? Detail) DescribeStep(VoiceCommand command)
    {
        var verb = ActionVerb(command);

        if (VoiceCommand.ActsOnEvents(command.Action))
            return (Compose(verb, DescribeEventQuantity(command)), DescribeEventStep(command));
        if (VoiceCommand.ActsOnTasks(command.Action))
            return (Compose(verb, DescribeTaskQuantity(command)), DescribeTaskStep(command));
        if (VoiceCommand.ActsOnContacts(command.Action))
            return (Compose(verb, DescribeContactQuantity(command)), DescribeContactStep(command));
        if (VoiceCommand.ActsOnFolder(command.Action))
            return DescribeFolderStep(command, verb);

        switch (command.Action)
        {
            case VoiceAction.CreateEvent when command.Event is { } details:
                return (Compose(verb, Plain(details.Title)), DescribeEvent(details));
            case VoiceAction.CreateTask when command.Task is { } task:
                return (Compose(verb, Plain(task.Title)), DescribeTask(task));
            case VoiceAction.CreateDraft when command.Draft is { } draft:
                return (Compose(verb, DescribeRecipients(draft)), Plain(draft.Subject));
            case VoiceAction.CreateTaskList:
                return (Compose(verb, Plain(command.TaskChange?.NewTitle)), null);
            case VoiceAction.RenameTaskList:
                return (Compose(verb, Plain(command.Tasks?.ListName)), Changed(command.TaskChange?.NewTitle));
            case VoiceAction.DeleteTaskList:
                return (Compose(verb, Plain(command.Tasks?.ListName)), null);
            case VoiceAction.CreateContact when command.ContactChange is { } contact:
                return (Compose(verb, Plain(contact.Name)), Join(Plain(contact.Email), Plain(contact.Phone)));
            case VoiceAction.Undo:
                return (Compose(verb, Translator.VoiceStep_Undo_Last), null);
            case VoiceAction.SyncMail:
                return (Compose(verb, Translator.VoiceStep_Sync_Mail), null);
        }

        var change = DescribeMailChange(command);
        if (command.UsesResultOf is not null)
            return (Compose(verb, Translator.VoiceStep_Mails_Same), Join(change, DescribeForwardRecipients(command)));

        if (command.Mail is not { } mail)
        {
            return command.Action == VoiceAction.Summarize
                ? (Compose(verb, Translator.VoiceStep_Mails_OpenMail), null)
                : (Compose(verb, Plain(command.Summary)), change);
        }

        var quantity = command.Action == VoiceAction.SendDrafts ? DescribeDraftQuantity(mail) : DescribeQuantity(mail);
        return (Compose(verb, quantity), Join(DescribeFilters(mail), change, DescribeForwardRecipients(command)));
    }

    private static string ActionVerb(VoiceCommand command) => command.Action switch
    {
        VoiceAction.SearchMail => Translator.VoiceStep_Action_SearchMail,
        VoiceAction.MarkRead => Translator.VoiceStep_Action_MarkRead,
        VoiceAction.MarkUnread => Translator.VoiceStep_Action_MarkUnread,
        VoiceAction.Flag => Translator.VoiceStep_Action_Flag,
        VoiceAction.Unflag => Translator.VoiceStep_Action_Unflag,
        VoiceAction.Archive => Translator.VoiceStep_Action_Archive,
        VoiceAction.Delete => Translator.VoiceStep_Action_Delete,
        VoiceAction.CreateEvent => Translator.VoiceStep_Action_CreateEvent,
        VoiceAction.CreateTask => Translator.VoiceStep_Action_CreateTask,
        VoiceAction.CreateDraft => Translator.VoiceStep_Action_CreateDraft,
        VoiceAction.CreateReplyDraft => command.Draft?.Kind switch
        {
            DraftKind.ReplyAll => Translator.VoiceStep_Action_CreateReplyAllDraft,
            DraftKind.Forward => Translator.VoiceStep_Action_Forward,
            _ => Translator.VoiceStep_Action_CreateReplyDraft
        },
        VoiceAction.Summarize => Translator.VoiceStep_Action_Summarize,
        VoiceAction.Unarchive => Translator.VoiceStep_Action_Unarchive,
        VoiceAction.FindEvents => Translator.VoiceStep_Action_FindEvents,
        VoiceAction.DeleteEvent => Translator.VoiceStep_Action_DeleteEvent,
        VoiceAction.UpdateEvent => Translator.VoiceStep_Action_UpdateEvent,
        VoiceAction.SetEventShowAs => Translator.VoiceStep_Action_SetEventShowAs,
        VoiceAction.RespondToEvent => command.Change?.Response switch
        {
            EventResponse.Accept => Translator.VoiceStep_Action_Accept,
            EventResponse.Tentative => Translator.VoiceStep_Action_Tentative,
            _ => Translator.VoiceStep_Action_Decline
        },
        VoiceAction.MoveToFolder => Translator.VoiceStep_Action_Move,
        VoiceAction.MarkJunk => Translator.VoiceStep_Action_MarkJunk,
        VoiceAction.MarkNotJunk => Translator.VoiceStep_Action_MarkNotJunk,
        VoiceAction.MoveToFocused or VoiceAction.MoveToOther => Translator.VoiceStep_Action_Move,
        VoiceAction.Pin => Translator.VoiceStep_Action_Pin,
        VoiceAction.Unpin => Translator.VoiceStep_Action_Unpin,
        VoiceAction.Categorize => Translator.VoiceStep_Action_Categorize,
        VoiceAction.Uncategorize => Translator.VoiceStep_Action_Uncategorize,
        VoiceAction.SendDrafts => Translator.VoiceStep_Action_Send,
        VoiceAction.Translate => Translator.VoiceStep_Action_Translate,
        VoiceAction.AnalyzeMail => Translator.VoiceStep_Action_Analyze,
        VoiceAction.CopyCode => Translator.VoiceStep_Action_CopyCode,
        VoiceAction.Unsubscribe => Translator.VoiceStep_Action_Unsubscribe,
        VoiceAction.Undo => Translator.VoiceStep_Action_Undo,
        VoiceAction.SyncMail or VoiceAction.SyncFolder => Translator.VoiceStep_Action_Sync,
        VoiceAction.SetEventReminder => Translator.VoiceStep_Action_SetReminder,
        VoiceAction.JoinMeeting => Translator.VoiceStep_Action_Join,
        VoiceAction.FindTasks or VoiceAction.FindContacts => Translator.VoiceStep_Action_SearchMail,
        VoiceAction.CompleteTask => Translator.VoiceStep_Action_Complete,
        VoiceAction.ReopenTask => Translator.VoiceStep_Action_Reopen,
        VoiceAction.UpdateTask => Translator.VoiceStep_Action_UpdateEvent,
        VoiceAction.AddTaskStep => Translator.VoiceStep_Action_AddStep,
        VoiceAction.DeleteTask or VoiceAction.DeleteContact or VoiceAction.DeleteFolder => Translator.VoiceStep_Action_Delete,
        VoiceAction.CreateTaskList => Translator.VoiceStep_Action_CreateTaskList,
        VoiceAction.RenameTaskList => Translator.VoiceStep_Action_RenameTaskList,
        VoiceAction.DeleteTaskList => Translator.VoiceStep_Action_DeleteTaskList,
        VoiceAction.FavoriteContact or VoiceAction.AddContactToList => Translator.VoiceStep_Action_Add,
        VoiceAction.UnfavoriteContact or VoiceAction.RemoveContactFromList => Translator.VoiceStep_Action_Remove,
        VoiceAction.CategorizeContact => Translator.VoiceStep_Action_Categorize,
        VoiceAction.CreateContact => Translator.VoiceStep_Action_CreateContact,
        VoiceAction.MarkFolderRead => Translator.VoiceStep_Action_MarkRead,
        VoiceAction.EmptyFolder => Translator.VoiceStep_Action_Empty,
        VoiceAction.PinFolder => Translator.VoiceStep_Action_Pin,
        VoiceAction.UnpinFolder => Translator.VoiceStep_Action_Unpin,
        VoiceAction.RenameFolder => Translator.VoiceStep_Action_Rename,
        VoiceAction.CreateFolder => Translator.VoiceStep_Action_CreateFolder,
        _ => Translator.VoiceStep_Action_NotActionable
    };

    /// <summary>What a mail action changes beyond its selection: the destination, the category, the language.</summary>
    private static string? DescribeMailChange(VoiceCommand command)
    {
        if (command.Action is VoiceAction.MoveToFocused or VoiceAction.MoveToOther)
        {
            var tab = command.Action == VoiceAction.MoveToFocused ? Translator.VoiceStep_Change_ToFocused : Translator.VoiceStep_Change_ToOther;
            return command.MailChange?.Always == true ? Join(tab, Translator.VoiceStep_Change_Always) : tab;
        }

        if (command.MailChange is not { } change)
            return null;

        return command.Action switch
        {
            VoiceAction.MoveToFolder => FolderName(change.FolderSpecialType, change.FolderName) is { } folder
                ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_ToFolder, folder)
                : null,
            VoiceAction.Categorize or VoiceAction.Uncategorize when !string.IsNullOrWhiteSpace(change.CategoryName)
                => string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Category, Plain(change.CategoryName)),
            VoiceAction.Translate when !string.IsNullOrWhiteSpace(change.Language)
                => string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Language, LanguageName(change.Language)),
            _ => null
        };
    }

    private static string DescribeDraftQuantity(MailSelection mail) => mail.Limit switch
    {
        1 => Translator.VoiceStep_Drafts_Latest,
        > 1 => string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Drafts_LatestCount, mail.Limit),
        _ => Translator.VoiceStep_Drafts_All
    };

    private static string? DescribeForwardRecipients(VoiceCommand command)
        => command.Action == VoiceAction.CreateReplyDraft && command.Draft is { Kind: DraftKind.Forward } draft && draft.To is { Count: > 0 }
            ? DescribeRecipients(draft)
            : null;

    public static string LanguageName(string code)
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

    private static string DescribeTaskQuantity(VoiceCommand command)
    {
        if (command.UsesResultOf is not null)
            return Translator.VoiceStep_Tasks_Same;

        var selection = command.Tasks;
        if (selection is { All: true })
            return Translator.VoiceStep_Tasks_All;
        if (selection is { Limit: > 1 } limited)
            return string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Tasks_Count, limited.Limit);
        if (command.Action == VoiceAction.FindTasks)
            return Translator.VoiceStep_Tasks_Any;

        return !string.IsNullOrWhiteSpace(selection?.Title)
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Tasks_Titled, Plain(selection.Title))
            : Translator.VoiceStep_Tasks_One;
    }

    /// <summary>The filters of a task step, then what it changes: "List “Home” · Overdue · Due Friday".</summary>
    private static string? DescribeTaskStep(VoiceCommand command)
    {
        var parts = new List<string>();
        if (command.Tasks is { } selection)
        {
            if (!string.IsNullOrWhiteSpace(selection.Title) && (selection.All || selection.Limit > 1 || command.Action == VoiceAction.FindTasks))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Filter_Subject, Plain(selection.Title)));
            if (!string.IsNullOrWhiteSpace(selection.ListName))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_List, Plain(selection.ListName)));
            if (selection.DueRange is { } range && !string.IsNullOrWhiteSpace(range.Phrase))
                parts.Add(Plain(range.Phrase));
            if (selection.Overdue)
                parts.Add(Translator.VoiceStep_Task_Overdue);
            if (selection.IsImportant == true)
                parts.Add(Translator.VoiceStep_Task_Important);
            if (selection.InMyDay == true)
                parts.Add(Translator.VoiceStep_Task_MyDay);
            if (selection.IncludeCompleted)
                parts.Add(Translator.VoiceStep_Task_WithCompleted);
        }

        if (command.TaskChange is { } change)
        {
            if (!string.IsNullOrWhiteSpace(change.NewTitle))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Title, Plain(change.NewTitle)));
            if (change.DueDate is { } due)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Task_Due, due.ToString("D", CultureInfo.CurrentCulture)));
            if (change.ClearDueDate)
                parts.Add(Translator.VoiceStep_Task_NoDueDate);
            if (change.IsImportant is bool important)
                parts.Add(important ? Translator.VoiceStep_Task_Important : Translator.VoiceStep_Task_NotImportant);
            if (change.InMyDay is bool myDay)
                parts.Add(myDay ? Translator.VoiceStep_Task_AddToMyDay : Translator.VoiceStep_Task_RemoveFromMyDay);
            if (!string.IsNullOrWhiteSpace(change.ListName))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_ToList, Plain(change.ListName)));
            if (!string.IsNullOrWhiteSpace(change.Step))
                parts.Add($"“{Plain(change.Step)}”");
            if (!string.IsNullOrWhiteSpace(change.Notes))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Task_Notes, Plain(change.Notes)));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string DescribeContactQuantity(VoiceCommand command)
    {
        if (command.UsesResultOf is not null)
            return Translator.VoiceStep_Contacts_Same;

        var selection = command.Contacts;
        if (!string.IsNullOrWhiteSpace(selection?.Name) && !selection.All && selection.Limit <= 1)
            return Plain(selection.Name);
        if (selection is { FavoritesOnly: true })
            return Translator.VoiceStep_Contacts_Favorites;
        if (selection is { All: true })
            return Translator.VoiceStep_Contacts_All;
        if (selection is { Limit: > 1 } limited)
            return string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Contacts_Count, limited.Limit);

        return command.Action == VoiceAction.FindContacts ? Translator.VoiceStep_Contacts_Any : Translator.VoiceStep_Contacts_One;
    }

    private static string? DescribeContactStep(VoiceCommand command)
    {
        var parts = new List<string>();
        if (command.Contacts is { } selection)
        {
            if (!string.IsNullOrWhiteSpace(selection.Name) && (selection.All || selection.Limit > 1))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Filter_Text, Plain(selection.Name)));
            if (!string.IsNullOrWhiteSpace(selection.ListName))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_List, Plain(selection.ListName)));
            if (!string.IsNullOrWhiteSpace(selection.CategoryName))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Category, Plain(selection.CategoryName)));
        }

        if (command.Action == VoiceAction.FavoriteContact)
            parts.Add(Translator.VoiceStep_Change_ToFavorites);
        else if (command.Action == VoiceAction.UnfavoriteContact)
            parts.Add(Translator.VoiceStep_Change_FromFavorites);

        if (command.ContactChange is { } change)
        {
            if (command.Action is VoiceAction.AddContactToList or VoiceAction.RemoveContactFromList && !string.IsNullOrWhiteSpace(change.ListName))
                parts.Add(string.Format(CultureInfo.CurrentCulture,
                    command.Action == VoiceAction.AddContactToList ? Translator.VoiceStep_Change_ToList : Translator.VoiceStep_Change_FromList,
                    Plain(change.ListName)));
            if (command.Action == VoiceAction.CategorizeContact && !string.IsNullOrWhiteSpace(change.CategoryName))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Category, Plain(change.CategoryName)));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static (string Text, string? Detail) DescribeFolderStep(VoiceCommand command, string verb)
    {
        var target = command.Folder;
        var folder = FolderName(target?.SpecialType, target?.FolderName);
        if (command.Action == VoiceAction.CreateFolder)
        {
            var parent = folder is null ? null : string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Folder_In, folder);
            return (Compose(verb, Plain(target?.NewName)), parent);
        }

        return (Compose(verb, folder ?? Plain(command.Summary)), command.Action == VoiceAction.RenameFolder ? Changed(target?.NewName) : null);
    }

    private static string? FolderName(SpecialFolderKind? kind, string? name) => kind switch
    {
        SpecialFolderKind.Inbox => Translator.VoiceStep_Folder_Inbox,
        SpecialFolderKind.Sent => Translator.VoiceStep_Folder_Sent,
        SpecialFolderKind.Draft => Translator.VoiceStep_Folder_Drafts,
        SpecialFolderKind.Archive => Translator.VoiceStep_Folder_Archive,
        SpecialFolderKind.Deleted => Translator.VoiceStep_Folder_Deleted,
        SpecialFolderKind.Junk => Translator.VoiceStep_Folder_Junk,
        SpecialFolderKind.Starred => Translator.VoiceStep_Folder_Starred,
        SpecialFolderKind.Important => Translator.VoiceStep_Folder_Important,
        _ => string.IsNullOrWhiteSpace(name) ? null : Plain(name)
    };

    private static string? Changed(string? newName)
        => string.IsNullOrWhiteSpace(newName) ? null : string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Title, Plain(newName));

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(static part => !string.IsNullOrWhiteSpace(part)).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }

    private static string DescribeEventQuantity(VoiceCommand command)
    {
        if (command.UsesResultOf is not null)
            return Translator.VoiceStep_Events_Same;

        var selection = command.Events;
        if (selection is { All: true })
            return Translator.VoiceStep_Events_All;
        if (selection is { Limit: > 1 } limited)
            return string.Format(CultureInfo.CurrentCulture, limited.Order == EventSortOrder.Latest ? Translator.VoiceStep_Events_LastCount : Translator.VoiceStep_Events_NextCount, limited.Limit);
        if (command.Action == VoiceAction.FindEvents)
            return Translator.VoiceStep_Events_Any;

        return !string.IsNullOrWhiteSpace(selection?.Title)
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Events_Titled, Plain(selection.Title))
            : Translator.VoiceStep_Events_One;
    }

    /// <summary>The filters of an event step, then what it changes: "With Jonas · tomorrow 1 p.m. · to 15:00".</summary>
    private static string? DescribeEventStep(VoiceCommand command)
    {
        var parts = new List<string>();
        if (command.Events is { } selection)
        {
            if (!string.IsNullOrWhiteSpace(selection.Title) && (selection.All || selection.Limit > 1 || command.Action == VoiceAction.FindEvents))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Filter_Subject, Plain(selection.Title)));
            if (!string.IsNullOrWhiteSpace(selection.Attendee))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Event_With, Plain(selection.Attendee)));
            if (selection.TimeRange is { } range && !string.IsNullOrWhiteSpace(range.Phrase))
                parts.Add(Plain(range.Phrase));
            if (!string.IsNullOrWhiteSpace(selection.CalendarName))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Event_Calendar, Plain(selection.CalendarName)));
        }

        if (command.Change is { } change)
        {
            if (change.NewDate is { } day)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Day, day.ToString("D", CultureInfo.CurrentCulture)));
            if (change.NewStartTime is { } time)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Time, time.ToString("t", CultureInfo.CurrentCulture)));
            if (change.ShiftMinutes is { } shift && shift != 0)
                parts.Add(string.Format(CultureInfo.CurrentCulture, shift > 0 ? Translator.VoiceStep_Change_Later : Translator.VoiceStep_Change_Earlier, DescribeMinutes(Math.Abs(shift))));
            if (change.DurationMinutes is { } length)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Length, DescribeMinutes(length)));
            if (!string.IsNullOrWhiteSpace(change.NewTitle))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Title, Plain(change.NewTitle)));
            if (!string.IsNullOrWhiteSpace(change.NewLocation))
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Location, Plain(change.NewLocation)));
            if (command.Action == VoiceAction.SetEventShowAs && change.ShowAs is { } showAs)
                parts.Add(ShowAsName(showAs));
            if (!string.IsNullOrWhiteSpace(change.ResponseMessage))
                parts.Add($"“{Plain(change.ResponseMessage)}”");
            if (command.Action == VoiceAction.SetEventReminder && change.ReminderMinutes is { } reminder)
                parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Change_Reminder, DescribeMinutes(reminder)));
            if (change.Scope == EventScope.Series)
                parts.Add(Translator.VoiceStep_Change_Series);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string DescribeMinutes(int minutes)
        => minutes % 1440 == 0 ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Days, minutes / 1440)
            : minutes % 60 == 0 ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Hours, minutes / 60)
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Minutes, minutes);

    public static string ShowAsName(EventShowAs showAs) => showAs switch
    {
        EventShowAs.Free => Translator.VoiceStep_ShowAs_Free,
        EventShowAs.Tentative => Translator.VoiceStep_ShowAs_Tentative,
        EventShowAs.OutOfOffice => Translator.VoiceStep_ShowAs_OutOfOffice,
        EventShowAs.WorkingElsewhere => Translator.VoiceStep_ShowAs_WorkingElsewhere,
        _ => Translator.VoiceStep_ShowAs_Busy
    };
    /// <summary>
    /// Says in the app's words why a part of the request can't be done. The planner's own question is not shown: the
    /// request is one-shot, so there is no turn to answer it in.
    /// </summary>
    private static string DescribeIssue(VoiceCommand command)
    {
        if (command.Reason == NotActionableReason.MissingDetails)
        {
            if (command.Missing is { } missing && missing.Contains(MissingDetail.Action) &&
                command.Alternatives is { Count: > 0 } alternatives)
            {
                var choices = alternatives.Select(ActionName).Where(static name => name.Length > 0).ToList();
                if (choices.Count > 0)
                    return string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_WhichAction, JoinChoices(choices));
            }

            if (command.Missing is { Count: > 0 } details)
            {
                var names = details.Where(static detail => detail != MissingDetail.Action).Select(MissingName).Where(static name => name.Length > 0).ToList();
                if (names.Count > 0)
                    return string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_Missing, string.Join(", ", names));
            }

            return Translator.VoiceIssue_MissingDetails;
        }

        return command.Reason switch
        {
            NotActionableReason.OutOfScope => Translator.VoiceIssue_OutOfScope,
            NotActionableReason.TooLong => Translator.VoiceIssue_TooLong,
            NotActionableReason.TooShort => Translator.VoiceIssue_TooShort,
            _ => Translator.VoiceIssue_NotUnderstood
        };
    }

    private static string ActionName(VoiceAction action) => action switch
    {
        VoiceAction.SearchMail => Translator.VoiceStep_Action_SearchMail,
        VoiceAction.MarkRead => Translator.VoiceStep_Action_MarkRead,
        VoiceAction.MarkUnread => Translator.VoiceStep_Action_MarkUnread,
        VoiceAction.Flag => Translator.VoiceStep_Action_Flag,
        VoiceAction.Unflag => Translator.VoiceStep_Action_Unflag,
        VoiceAction.Archive => Translator.VoiceStep_Action_Archive,
        VoiceAction.Delete => Translator.VoiceStep_Action_Delete,
        VoiceAction.CreateEvent => Translator.VoiceStep_Action_CreateEvent,
        VoiceAction.CreateTask => Translator.VoiceStep_Action_CreateTask,
        VoiceAction.CreateDraft => Translator.VoiceStep_Action_CreateDraft,
        VoiceAction.Summarize => Translator.VoiceStep_Action_Summarize,
        VoiceAction.Unarchive => Translator.VoiceStep_Action_Unarchive,
        VoiceAction.DeleteEvent => Translator.VoiceStep_Action_DeleteEvent,
        VoiceAction.UpdateEvent => Translator.VoiceStep_Action_UpdateEvent,
        VoiceAction.MoveToFolder => Translator.VoiceStep_Action_Move,
        VoiceAction.MarkJunk => Translator.VoiceStep_Action_MarkJunk,
        VoiceAction.Pin => Translator.VoiceStep_Action_Pin,
        VoiceAction.Categorize => Translator.VoiceStep_Action_Categorize,
        VoiceAction.Unsubscribe => Translator.VoiceStep_Action_Unsubscribe,
        VoiceAction.CompleteTask => Translator.VoiceStep_Action_Complete,
        VoiceAction.EmptyFolder => Translator.VoiceStep_Action_Empty,
        _ => string.Empty
    };

    private static string MissingName(MissingDetail detail) => detail switch
    {
        MissingDetail.Date => Translator.VoiceIssue_Missing_Date,
        MissingDetail.Time => Translator.VoiceIssue_Missing_Time,
        MissingDetail.Title => Translator.VoiceIssue_Missing_Title,
        MissingDetail.Recipient => Translator.VoiceIssue_Missing_Recipient,
        MissingDetail.Content => Translator.VoiceIssue_Missing_Content,
        MissingDetail.Mail => Translator.VoiceIssue_Missing_Mail,
        MissingDetail.Event => Translator.VoiceIssue_Missing_Event,
        MissingDetail.Change => Translator.VoiceIssue_Missing_Change,
        MissingDetail.Folder => Translator.VoiceIssue_Missing_Folder,
        MissingDetail.Category => Translator.VoiceIssue_Missing_Category,
        MissingDetail.Language => Translator.VoiceIssue_Missing_Language,
        MissingDetail.TaskList => Translator.VoiceIssue_Missing_TaskList,
        MissingDetail.Task => Translator.VoiceIssue_Missing_Task,
        MissingDetail.Contact => Translator.VoiceIssue_Missing_Contact,
        _ => string.Empty
    };

    private static string JoinChoices(IReadOnlyList<string> choices)
        => choices.Count == 1
            ? choices[0]
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceIssue_ChoiceList, string.Join(", ", choices.Take(choices.Count - 1)), choices[^1]);
    private static string Compose(string verb, string? subject)
        => string.IsNullOrWhiteSpace(subject)
            ? $"**{Plain(verb)}**"
            : string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_ActionAndSubject, Plain(verb), subject);

    /// <summary>Drops the strong markers from text that came from the user or the planner, so only the action is bold.</summary>
    private static string Plain(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace("**", string.Empty).Trim();

    private static string DescribeQuantity(MailSelection mail) => mail.Limit switch
    {
        1 => mail.Order == MailSortOrder.Oldest ? Translator.VoiceStep_Mails_Oldest : Translator.VoiceStep_Mails_Latest,
        > 1 => string.Format(CultureInfo.CurrentCulture, mail.Order == MailSortOrder.Oldest ? Translator.VoiceStep_Mails_OldestCount : Translator.VoiceStep_Mails_LatestCount, mail.Limit),
        _ => Translator.VoiceStep_Mails_All
    };

    private static string? DescribeFilters(MailSelection mail)
    {
        var parts = new List<string>();

        switch (mail.ReadStatus)
        {
            case ReadStatusFilter.Unread:
                parts.Add(Translator.VoiceStep_Filter_Unread);
                break;
            case ReadStatusFilter.Read:
                parts.Add(Translator.VoiceStep_Filter_Read);
                break;
        }

        if (!string.IsNullOrWhiteSpace(mail.Sender))
            parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Filter_From, Plain(mail.Sender)));
        if (!string.IsNullOrWhiteSpace(mail.Subject))
            parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Filter_Subject, Plain(mail.Subject)));
        if (!string.IsNullOrWhiteSpace(mail.Text))
            parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Filter_Text, Plain(mail.Text)));

        var folder = DescribeFolder(mail);
        if (folder is not null)
            parts.Add(folder);

        if (mail.DateRange is { } range && !string.IsNullOrWhiteSpace(range.Phrase))
            parts.Add(Plain(range.Phrase));
        if (mail.HasAttachments is bool hasAttachments)
            parts.Add(hasAttachments ? Translator.VoiceStep_Filter_WithAttachments : Translator.VoiceStep_Filter_WithoutAttachments);
        switch (mail.SenderKind)
        {
            case MailSenderKind.Person:
                parts.Add(Translator.VoiceStep_Filter_FromPeople);
                break;
            case MailSenderKind.Service:
                parts.Add(Translator.VoiceStep_Filter_Automated);
                break;
            case MailSenderKind.Newsletter:
                parts.Add(Translator.VoiceStep_Filter_Newsletters);
                break;
        }
        if (mail.IsFlagged is bool isFlagged)
            parts.Add(isFlagged ? Translator.VoiceStep_Filter_Flagged : Translator.VoiceStep_Filter_NotFlagged);

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? DescribeFolder(MailSelection mail)
    {
        if (mail.Scope == FolderScope.AllFolders)
            return Translator.VoiceStep_Folder_All;

        var name = mail.FolderSpecialType switch
        {
            SpecialFolderKind.Inbox => Translator.VoiceStep_Folder_Inbox,
            SpecialFolderKind.Sent => Translator.VoiceStep_Folder_Sent,
            SpecialFolderKind.Draft => Translator.VoiceStep_Folder_Drafts,
            SpecialFolderKind.Archive => Translator.VoiceStep_Folder_Archive,
            SpecialFolderKind.Deleted => Translator.VoiceStep_Folder_Deleted,
            SpecialFolderKind.Junk => Translator.VoiceStep_Folder_Junk,
            SpecialFolderKind.Starred => Translator.VoiceStep_Folder_Starred,
            SpecialFolderKind.Important => Translator.VoiceStep_Folder_Important,
            _ => string.IsNullOrWhiteSpace(mail.FolderName) ? null : Plain(mail.FolderName)
        };

        if (name is null)
            return null;

        return mail.Scope == FolderScope.Subfolders
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Folder_WithSubfolders, name)
            : name;
    }

    private static string? DescribeEvent(EventDetails details)
    {
        var when = details.IsAllDay
            ? details.StartLocal.ToString("D", CultureInfo.CurrentCulture)
            : details.StartLocal.ToString("f", CultureInfo.CurrentCulture);
        var parts = new List<string> { when };
        if (details.ShowAs is { } showAs)
            parts.Add(ShowAsName(showAs));
        if (!string.IsNullOrWhiteSpace(details.Location))
            parts.Add(Plain(details.Location));
        if (details.Attendees is { Count: > 0 } attendees)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Event_With, string.Join(", ", attendees.Select(Plain))));
        return string.Join(" · ", parts);
    }

    private static string? DescribeTask(TaskDetails task)
    {
        var parts = new List<string>();
        if (task.DueDate is { } due)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Task_Due, due.ToString("D", CultureInfo.CurrentCulture)));
        if (task.IsImportant)
            parts.Add(Translator.VoiceStep_Task_Important);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string DescribeRecipients(DraftDetails draft)
        => draft.To is { Count: > 0 } to
            ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceStep_Draft_To, string.Join(", ", to.Select(Plain)))
            : string.Empty;

    private sealed record Resolution(VoicePlanAccountKind Kind, MailAccount? Account, IReadOnlyList<MailAccount> Candidates, string? Spoken, string Key)
    {
        public static Resolution None { get; } = new(VoicePlanAccountKind.None, null, Array.Empty<MailAccount>(), null, "none");

        public static Resolution ForAccount(MailAccount account) => new(VoicePlanAccountKind.Account, account, new[] { account }, null, account.Id.ToString());

        public static Resolution Unresolved(IReadOnlyList<MailAccount> candidates, string? spoken)
            => new(VoicePlanAccountKind.Unresolved, null, candidates, spoken, "unresolved:" + spoken);
    }

    private sealed class GroupBuilder(Resolution resolution)
    {
        public Resolution Resolution { get; } = resolution;

        public List<VoicePlanPreviewStep> Steps { get; } = [];

        public List<VoicePlanPreviewIssue> Issues { get; } = [];
    }
}
