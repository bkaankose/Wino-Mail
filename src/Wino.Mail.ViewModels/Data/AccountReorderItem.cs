using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// One row of the account reorder dialog: a group with its own reorderable accounts, or an account.
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
        IconGlyph = AccountGroupIcons.GetGlyph(entry.Group?.Icon ?? AccountGroupIcon.PeopleLink);
        Subtitle = entry.Kind == AccountLayoutEntryKind.Group
            ? AccountGroupPresentation.GetAccountCountText(entry.GetAccounts().Count)
            : entry.Account?.Address ?? string.Empty;

        foreach (var child in entry.Children)
        {
            Children.Add(new AccountReorderItem(child));
        }
    }

    public Guid Id { get; }

    public AccountLayoutEntryKind Kind { get; }

    public bool IsGroup => Kind == AccountLayoutEntryKind.Group;

    public string Title { get; }

    public string Subtitle { get; }

    public MailAccount? Account { get; }

    public string? ColorKey { get; }

    public WinoIconGlyph IconGlyph { get; }

    public string GroupEntriesAutomationName => string.Format(Translator.AccountReorderDialog_GroupEntries, Title);

    /// <summary>The accounts of a group, in their current order.</summary>
    public ObservableCollection<AccountReorderItem> Children { get; } = [];

    public static ObservableCollection<AccountReorderItem> FromLayout(IEnumerable<AccountLayoutEntry> layout)
        => new((layout ?? Enumerable.Empty<AccountLayoutEntry>()).Select(entry => new AccountReorderItem(entry)));

    /// <summary>The order to save: top-level rows with the ordered accounts of each group.</summary>
    public static List<AccountLayoutOrderEntry> ToOrder(IEnumerable<AccountReorderItem> items)
        => (items ?? Enumerable.Empty<AccountReorderItem>())
            .Select(item => new AccountLayoutOrderEntry(item.Id, item.Children.Select(child => child.Id).ToList()))
            .ToList();
}
