#nullable enable
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// An account another mail client on this device holds, mapped to the provider Wino uses for it. It carries no password
/// and no token: an imported account waits for the user to sign in.
/// </summary>
public sealed class MailClientImportCandidate
{
    public MailClientImportSource Source { get; init; }

    /// <summary>The name the other client shows for the account; the address when it has none.</summary>
    public string AccountName { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    /// <summary>The sender name the other client sends with; empty when it has none.</summary>
    public string SenderName { get; init; } = string.Empty;

    public MailProviderType ProviderType { get; init; }
    public SpecialImapProvider SpecialImapProvider { get; init; }

    /// <summary>
    /// Servers for IMAP, POP3 and Exchange accounts, without passwords. Null for Outlook and Gmail, and for an account
    /// whose servers the other client does not keep on this device; those are discovered when the account is imported.
    /// </summary>
    public CustomServerInformation? ServerInformation { get; init; }

    /// <summary>Wino already has an account with this address; importing skips it.</summary>
    public bool IsAlreadyAdded { get; init; }
}
