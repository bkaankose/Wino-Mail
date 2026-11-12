using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One row of the account reorder dialog: a group with its own reorderable entries, a linked account or an account.
/// Rows only change order inside their own list; membership changes on the group page.
/// </summary>
public sealed class AccountReorderItem
{
    private AccountReorderItem(AccountLayoutEntry entry)
    {
        Id = entry.Id;
        Kind = entry.Kind;
        Title = entry.Name;
        Account = entry.Account;
        ColorKey = AccountGroupColors.Normalize(entry.Group?.ColorKey);
        Subtitle = entry.Kind switch
        {
            AccountLayoutEntryKind.Group => AccountGroupPresentation.GetAccountCountText(entry.GetAccounts().Count()),
            AccountLayoutEntryKind.LinkedAccount => string.Join(", ", entry.Members.Select(member => member.Address)),
            _ => entry.Account?.Address ?? string.Empty
        };

        foreach (var child in entry.Children)
        {
            Children.Add(new AccountReorderItem(child));
        }
    }

    public Guid Id { get; }

    public AccountLayoutEntryKind Kind { get; }

    public bool IsGroup => Kind == AccountLayoutEntryKind.Group;

    public bool IsLinkedAccount => Kind == AccountLayoutEntryKind.LinkedAccount;

    public string Title { get; }

    public string Subtitle { get; }

    public MailAccount? Account { get; }

    public string? ColorKey { get; }

    public string GroupEntriesAutomationName => string.Format(Translator.AccountReorderDialog_GroupEntries, Title);

    /// <summary>The entries of a group, in their current order.</summary>
    public ObservableCollection<AccountReorderItem> Children { get; } = [];

    public string MoveUpAutomationName => string.Format(Translator.Accessibility_ItemAction, Translator.AccountGroupDetailsPage_MoveUp, Title);

    public string MoveDownAutomationName => string.Format(Translator.Accessibility_ItemAction, Translator.AccountGroupDetailsPage_MoveDown, Title);

    public static ObservableCollection<AccountReorderItem> FromLayout(IEnumerable<AccountLayoutEntry> layout)
        => new((layout ?? Enumerable.Empty<AccountLayoutEntry>()).Select(entry => new AccountReorderItem(entry)));

    /// <summary>The order to save: top-level rows with the ordered entries of each group.</summary>
    public static List<AccountLayoutOrderEntry> ToOrder(IEnumerable<AccountReorderItem> items)
        => (items ?? Enumerable.Empty<AccountReorderItem>())
            .Select(item => new AccountLayoutOrderEntry(item.Id, item.Children.Select(child => child.Id).ToList()))
            .ToList();

    /// <summary>Moves <paramref name="item"/> one place within whichever list of <paramref name="rootItems"/> holds it.</summary>
    public static void Move(ObservableCollection<AccountReorderItem> rootItems, AccountReorderItem item, int offset)
    {
        var owner = rootItems.Contains(item)
            ? rootItems
            : rootItems.FirstOrDefault(root => root.Children.Contains(item))?.Children;

        if (owner == null)
            return;

        var index = owner.IndexOf(item);
        var target = index + offset;

        if (index < 0 || target < 0 || target >= owner.Count)
            return;

        owner.Move(index, target);
    }
}
