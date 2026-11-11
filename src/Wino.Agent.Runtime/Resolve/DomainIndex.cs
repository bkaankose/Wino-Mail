using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

/// <summary>One turn's snapshot of the device's structure: accounts with their aliases, folders, calendars, lists, categories and contact lists.</summary>
public sealed class DomainIndex
{
    public sealed record AccountEntry(MailAccount Account, IReadOnlyList<string> Aliases, IReadOnlyList<MailItemFolder> Folders,
        IReadOnlyList<AccountCalendar> Calendars, IReadOnlyList<AccountTaskList> TaskLists, IReadOnlyList<MailCategory> Categories)
    {
        public Guid Id => Account.Id;
        public string Address => Account.Address ?? "";
        public string Name => Account.Name ?? "";
        public IEnumerable<string> AllAddresses => new[] { Address }.Concat(Aliases).Where(a => !string.IsNullOrEmpty(a));
    }

    /// <summary>A container of any type with the words it can be found by.</summary>
    public sealed record Container(ItemKind Kind, Guid AccountId, Guid Id, string Name, string? Path, SpecialFolderType? Special, bool IsPrimary, bool IsDefault, bool ReadOnly)
    {
        public ItemRef Ref => new(Kind, AccountId, Id.ToString());
    }

    public IReadOnlyList<AccountEntry> Accounts { get; private init; } = [];
    public IReadOnlyList<ContactList> ContactLists { get; private init; } = [];
    public IReadOnlyList<Container> Containers { get; private init; } = [];

    public AccountEntry? Account(Guid? id) => id is null ? null : Accounts.FirstOrDefault(a => a.Id == id);

    public static async Task<DomainIndex> LoadAsync(IAgentDomainQueries queries, CancellationToken ct)
    {
        var accounts = new List<AccountEntry>();
        var containers = new List<Container>();
        foreach (var account in (await queries.GetAccountsAsync(ct).ConfigureAwait(false)).OrderBy(a => a.Order))
        {
            var aliases = (await queries.GetAliasesAsync(account.Id, ct).ConfigureAwait(false)).Select(a => a.AliasAddress).Where(a => !string.IsNullOrEmpty(a)).ToList();
            var folders = await queries.GetFoldersAsync(account.Id, ct).ConfigureAwait(false);
            var calendars = await queries.GetCalendarsAsync(account.Id, ct).ConfigureAwait(false);
            var lists = await queries.GetTaskListsAsync(account.Id, ct).ConfigureAwait(false);
            var categories = await queries.GetCategoriesAsync(account.Id, ct).ConfigureAwait(false);
            accounts.Add(new AccountEntry(account, aliases, folders, calendars, lists, categories));
            containers.AddRange(folders.Where(f => !f.IsHidden && f.SpecialFolderType is not (SpecialFolderType.More or SpecialFolderType.Category))
                .Select(f => new Container(ItemKind.Folder, account.Id, f.Id, f.ResolvedDisplayName ?? "", f.FolderName, f.SpecialFolderType, false, false, false)));
            containers.AddRange(calendars.Select(c => new Container(ItemKind.Calendar, account.Id, c.Id, c.Name ?? "", null, null, c.IsPrimary, c.IsPrimary, c.IsReadOnly)));
            containers.AddRange(lists.Select(l => new Container(ItemKind.TaskList, account.Id, l.Id, l.Title ?? "", null, null, l.IsDefault, l.IsDefault, l.IsReadOnly)));
            containers.AddRange(categories.Select(c => new Container(ItemKind.Category, account.Id, c.Id, c.Name ?? "", null, null, false, false, false)));
        }

        var contactLists = await queries.GetContactListsAsync(ct).ConfigureAwait(false);
        containers.AddRange(contactLists.Select(l => new Container(ItemKind.ContactList, l.MailAccountId ?? Guid.Empty, l.Id, l.Name ?? "", null, null, false, false, false)));
        return new DomainIndex { Accounts = accounts, ContactLists = contactLists, Containers = containers };
    }

    public static SpecialFolderType? SpecialOf(string wellKnown) => wellKnown switch
    {
        AssistantWellKnown.Inbox => SpecialFolderType.Inbox,
        AssistantWellKnown.Sent => SpecialFolderType.Sent,
        AssistantWellKnown.Archive => SpecialFolderType.Archive,
        AssistantWellKnown.Trash => SpecialFolderType.Deleted,
        AssistantWellKnown.Junk => SpecialFolderType.Junk,
        AssistantWellKnown.Drafts => SpecialFolderType.Draft,
        AssistantWellKnown.Starred => SpecialFolderType.Starred,
        AssistantWellKnown.Important => SpecialFolderType.Important,
        _ => null,
    };

    public IEnumerable<Container> ContainersOf(ItemKind kind, IReadOnlyList<Guid>? accountScope)
        => Containers.Where(c => c.Kind == kind && (accountScope is null || accountScope.Count == 0 || accountScope.Contains(c.AccountId)));

    public IEnumerable<Container> SpecialFolders(SpecialFolderType special, IReadOnlyList<Guid>? accountScope)
        => ContainersOf(ItemKind.Folder, accountScope).Where(c => c.Special == special);

    public Container? Primary(ItemKind kind, Guid accountId) => Containers.FirstOrDefault(c => c.Kind == kind && c.AccountId == accountId && c.IsPrimary);

    public string? AccountName(Guid? accountId) => Account(accountId)?.Name;
}
