using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Mail;

/// <summary>
/// An account group. Its members carry its id in <see cref="Shared.MailAccount.MergedInboxId"/>, and it sits
/// in the account list where its first member falls in <see cref="Shared.MailAccount.Order"/>.
/// </summary>
public class MergedInbox
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; }

    /// <summary>
    /// Stored inverted so the 0 or NULL that existing rows get when the column is added keeps them combined,
    /// the only behavior groups had before.
    /// </summary>
    public bool SeparateFolders { get; set; }

    /// <summary>Key of an <see cref="Models.Accounts.AccountGroupColors"/> entry, or null for the theme colors.</summary>
    public string? ColorKey { get; set; }

    public AccountGroupIcon Icon { get; set; }

    /// <summary>Stored inverted so existing rows start expanded.</summary>
    public bool IsCollapsed { get; set; }

    /// <summary>
    /// The group is one selectable inbox whose folders are combined across its accounts. Otherwise it only
    /// gathers its accounts under a collapsible row.
    /// </summary>
    [Ignore]
    public bool CombineFolders
    {
        get => !SeparateFolders;
        set => SeparateFolders = !value;
    }
}
