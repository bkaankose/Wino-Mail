using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Accounts;

public enum AccountLayoutEntryKind
{
    Account,
    LinkedAccount,
    Group
}

/// <summary>
/// One entry of the account layout: an account, a linked account with its members, or a group with its entries.
/// </summary>
public sealed class AccountLayoutEntry
{
    private AccountLayoutEntry(AccountLayoutEntryKind kind, Guid id)
    {
        Kind = kind;
        Id = id;
    }

    public AccountLayoutEntryKind Kind { get; }

    /// <summary>The account id, merged inbox id or group id.</summary>
    public Guid Id { get; }

    public MailAccount? Account { get; private init; }

    public MergedInbox? LinkedAccount { get; private init; }

    public MailAccountGroup? Group { get; private init; }

    /// <summary>The member accounts of a linked account, in layout order.</summary>
    public List<MailAccount> Members { get; } = [];

    /// <summary>The entries of a group, in layout order.</summary>
    public List<AccountLayoutEntry> Children { get; } = [];

    public string Name => Kind switch
    {
        AccountLayoutEntryKind.Group => Group?.Name ?? string.Empty,
        AccountLayoutEntryKind.LinkedAccount => LinkedAccount?.Name ?? string.Empty,
        _ => Account?.Name ?? string.Empty
    };

    /// <summary>Every account the entry holds, in layout order.</summary>
    public IEnumerable<MailAccount> GetAccounts() => Kind switch
    {
        AccountLayoutEntryKind.Account => Account is null ? Array.Empty<MailAccount>() : new[] { Account },
        AccountLayoutEntryKind.LinkedAccount => Members,
        _ => Children.SelectMany(child => child.GetAccounts())
    };

    public static AccountLayoutEntry ForAccount(MailAccount account)
        => new(AccountLayoutEntryKind.Account, account.Id) { Account = account };

    public static AccountLayoutEntry ForLinkedAccount(Guid mergedInboxId, MergedInbox? mergedInbox)
        => new(AccountLayoutEntryKind.LinkedAccount, mergedInboxId) { LinkedAccount = mergedInbox };

    public static AccountLayoutEntry ForGroup(MailAccountGroup group)
        => new(AccountLayoutEntryKind.Group, group.Id) { Group = group };
}

/// <summary>A top-level entry of a saved order, with the ordered entry ids of a group.</summary>
public sealed record AccountLayoutOrderEntry(Guid Id, List<Guid> ChildIds);

/// <summary>
/// Turns accounts and groups into the ordered top-level entries every navigation pane and the settings pages show.
/// A group sits where its first account falls in the given order, and a linked account where its first member falls.
/// </summary>
public static class AccountLayoutBuilder
{
    /// <param name="orderedAccounts">Accounts in <see cref="MailAccount.Order"/>; the order is used as given.</param>
    /// <param name="groups">The groups; an account whose group is missing stays at the top level.</param>
    /// <param name="includeLinkedAccounts">False lists the members of a linked account as individual accounts.</param>
    public static List<AccountLayoutEntry> Build(IEnumerable<MailAccount> orderedAccounts,
                                                 IEnumerable<MailAccountGroup> groups,
                                                 bool includeLinkedAccounts = true)
    {
        var groupsById = (groups ?? Enumerable.Empty<MailAccountGroup>()).Where(group => group != null)
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var topLevel = new List<AccountLayoutEntry>();
        var groupEntries = new Dictionary<Guid, AccountLayoutEntry>();
        var linkedEntries = new Dictionary<Guid, AccountLayoutEntry>();

        foreach (var account in orderedAccounts ?? Enumerable.Empty<MailAccount>())
        {
            if (account == null)
                continue;

            var container = topLevel;

            if (account.MailAccountGroupId is Guid groupId && groupsById.TryGetValue(groupId, out var group))
            {
                if (!groupEntries.TryGetValue(groupId, out var groupEntry))
                {
                    groupEntry = AccountLayoutEntry.ForGroup(group);
                    groupEntries.Add(groupId, groupEntry);
                    topLevel.Add(groupEntry);
                }

                container = groupEntry.Children;
            }

            if (includeLinkedAccounts && account.MergedInboxId is Guid mergedInboxId)
            {
                if (!linkedEntries.TryGetValue(mergedInboxId, out var linkedEntry))
                {
                    linkedEntry = AccountLayoutEntry.ForLinkedAccount(mergedInboxId, account.MergedInbox);
                    linkedEntries.Add(mergedInboxId, linkedEntry);
                    container.Add(linkedEntry);
                }

                linkedEntry.Members.Add(account);
                continue;
            }

            container.Add(AccountLayoutEntry.ForAccount(account));
        }

        return topLevel;
    }

    /// <summary>The accounts of <paramref name="entries"/> in layout order.</summary>
    public static List<MailAccount> Flatten(IEnumerable<AccountLayoutEntry> entries)
        => (entries ?? Enumerable.Empty<AccountLayoutEntry>()).SelectMany(entry => entry.GetAccounts()).ToList();
}
