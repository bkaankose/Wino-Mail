using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Models.Accounts;

public enum AccountLayoutEntryKind
{
    Account,
    Group
}

/// <summary>
/// One entry of the account layout: an account, or an account group with its accounts.
/// </summary>
public sealed class AccountLayoutEntry
{
    private AccountLayoutEntry(AccountLayoutEntryKind kind, Guid id)
    {
        Kind = kind;
        Id = id;
    }

    public AccountLayoutEntryKind Kind { get; }

    /// <summary>The account id or the group (merged inbox) id.</summary>
    public Guid Id { get; }

    public MailAccount? Account { get; private init; }

    public MergedInbox? Group { get; private init; }

    /// <summary>The account entries of a group, in layout order.</summary>
    public List<AccountLayoutEntry> Children { get; } = [];

    public string Name => Kind == AccountLayoutEntryKind.Group
        ? Group?.Name ?? string.Empty
        : Account?.Name ?? string.Empty;

    /// <summary>Every account the entry holds, in layout order.</summary>
    public List<MailAccount> GetAccounts() => Kind == AccountLayoutEntryKind.Account
        ? Account is null ? [] : [Account]
        : Children.SelectMany(child => child.GetAccounts()).ToList();

    public static AccountLayoutEntry ForAccount(MailAccount account)
        => new(AccountLayoutEntryKind.Account, account.Id) { Account = account };

    public static AccountLayoutEntry ForGroup(MergedInbox group)
        => new(AccountLayoutEntryKind.Group, group.Id) { Group = group };
}

/// <summary>A top-level entry of a saved order, with the ordered account ids of a group.</summary>
public sealed record AccountLayoutOrderEntry(Guid Id, List<Guid> ChildIds);

/// <summary>
/// Turns accounts and groups into the ordered top-level entries every navigation pane and the settings pages show.
/// A group sits where its first account falls in the given order.
/// </summary>
public static class AccountLayoutBuilder
{
    /// <param name="orderedAccounts">Accounts in <see cref="MailAccount.Order"/>; the order is used as given.</param>
    /// <param name="groups">The groups; an account whose group is missing stays at the top level.</param>
    public static List<AccountLayoutEntry> Build(IEnumerable<MailAccount> orderedAccounts, IEnumerable<MergedInbox> groups)
    {
        var groupsById = (groups ?? Enumerable.Empty<MergedInbox>())
            .Where(group => group != null)
            .GroupBy(group => group.Id)
            .ToDictionary(group => group.Key, group => group.First());

        var topLevel = new List<AccountLayoutEntry>();
        var groupEntries = new Dictionary<Guid, AccountLayoutEntry>();

        foreach (var account in orderedAccounts ?? Enumerable.Empty<MailAccount>())
        {
            if (account == null)
                continue;

            if (account.MergedInboxId is Guid groupId && groupsById.TryGetValue(groupId, out var group))
            {
                if (!groupEntries.TryGetValue(groupId, out var groupEntry))
                {
                    groupEntry = AccountLayoutEntry.ForGroup(group);
                    groupEntries.Add(groupId, groupEntry);
                    topLevel.Add(groupEntry);
                }

                groupEntry.Children.Add(AccountLayoutEntry.ForAccount(account));
                continue;
            }

            topLevel.Add(AccountLayoutEntry.ForAccount(account));
        }

        return topLevel;
    }

    /// <summary>The accounts of <paramref name="entries"/> in layout order.</summary>
    public static List<MailAccount> Flatten(IEnumerable<AccountLayoutEntry> entries)
        => (entries ?? Enumerable.Empty<AccountLayoutEntry>()).SelectMany(entry => entry.GetAccounts()).ToList();
}
