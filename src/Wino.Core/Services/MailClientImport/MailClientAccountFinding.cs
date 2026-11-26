#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Core.Services.MailClientImport;

internal enum MailClientAccountKind
{
    /// <summary>The client named no protocol; the address and the catalog decide.</summary>
    Unknown,
    Imap,
    Pop3,
    Exchange,
    Outlook,
    Gmail
}

/// <summary>One account as a client stores it, before Wino maps it to a provider.</summary>
internal sealed class MailClientAccountFinding
{
    public MailClientImportSource Source { get; init; }
    public MailClientAccountKind Kind { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;

    public MailClientServerEndpoint? Incoming { get; set; }
    public MailClientServerEndpoint? Outgoing { get; set; }

    /// <summary>For Exchange: the EWS URL when the client stores one, otherwise the server host.</summary>
    public string ExchangeServer { get; set; } = string.Empty;

    public ExchangeTransport ExchangeTransport { get; set; } = ExchangeTransport.Automatic;
}

internal sealed class MailClientServerEndpoint
{
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }
    public ImapConnectionSecurity Security { get; set; } = ImapConnectionSecurity.Auto;
    public ImapAuthenticationMethod Authentication { get; set; } = ImapAuthenticationMethod.Auto;
    public string Username { get; set; } = string.Empty;

    public bool HasHost => !string.IsNullOrWhiteSpace(Host);
}
