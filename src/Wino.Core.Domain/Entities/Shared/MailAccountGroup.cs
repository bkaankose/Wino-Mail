using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// A user-named container for accounts and linked accounts in the navigation panes. Membership is
/// <see cref="MailAccount.MailAccountGroupId"/>; the group sits where its first account falls in <see cref="MailAccount.Order"/>.
/// </summary>
public class MailAccountGroup
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string Name { get; set; }

    /// <summary>Key of an <see cref="Models.Accounts.AccountGroupColors"/> entry, or null for the default text color.</summary>
    public string ColorKey { get; set; }

    public bool IsExpanded { get; set; } = true;
}
