#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

public static class AccountRouteDeadlines
{
    public static readonly TimeSpan Soft = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan Hard = TimeSpan.FromSeconds(20);
}

public enum AccountRoute
{
    NotFound,
    Gmail,
    Outlook,
    Imap,

    /// <summary>On-premises Exchange Server, found by Exchange Autodiscover (<see cref="AccountRouteResolution.Exchange"/>).</summary>
    Exchange
}

/// <summary>Which step of the resolution decided the route.</summary>
public enum AccountRouteEvidence
{
    None,

    /// <summary>A Google or Microsoft consumer domain (gmail.com, outlook.com...).</summary>
    Domain,

    /// <summary>A catalog provider's own domain.</summary>
    Catalog,

    /// <summary>The domain's MX host is Google Workspace or Microsoft 365.</summary>
    Mx,

    /// <summary>Microsoft's user-realm endpoint knows the address (Microsoft 365 behind a mail filter).</summary>
    UserRealm,

    /// <summary>Autodiscovery found the IMAP host.</summary>
    ImapHost,

    /// <summary>Exchange Autodiscover found the mailbox's Exchange server (on premises, or Microsoft 365 for the Outlook route).</summary>
    ExchangeAutodiscover
}

public enum AccountRouteResolveStage
{
    /// <summary>"Checking who hosts {domain}" (MX lookup).</summary>
    CheckingDomainHost,

    /// <summary>"Asking Microsoft whether {domain} is a Microsoft 365 domain". The typed address is sent to Microsoft.</summary>
    AskingMicrosoft,

    /// <summary>"Finding the mail servers".</summary>
    FindingServers,

    /// <summary>"Checking how the server accepts sign-in".</summary>
    CheckingSignIn,

    /// <summary>"Looking for the provider's sign-in page".</summary>
    LookingForSignInPage,

    /// <summary>"Looking for an Exchange server". Reported only while the resolution waits for Exchange Autodiscover.</summary>
    LookingForExchange
}

/// <summary>
/// One progress line. <see cref="IsSlow"/> turns true once, at the soft deadline, with the stage still running
/// ("Still looking… Set up manually instead").
/// </summary>
public sealed record AccountRouteProgress(AccountRouteResolveStage Stage, string Domain, bool IsSlow);

public sealed record AccountRouteRequest
{
    public string Address { get; init; } = string.Empty;
    public bool DiscoverCalendar { get; init; } = true;
    public bool DiscoverContacts { get; init; } = true;

    /// <summary>False on heads without the secure sign-in surface; the IMAP browser option is then never offered.</summary>
    public bool AllowBrowserSignIn { get; init; } = true;

    /// <summary>
    /// The user's provider choice, which skips detection: Gmail and Outlook resolve without a network call, Imap runs
    /// only IMAP discovery (or uses <see cref="ForcedImapProvider"/> from the catalog), Exchange runs only Exchange
    /// Autodiscover. The result is marked <see cref="AccountRouteResolution.IsManual"/>.
    /// </summary>
    public AccountRoute? ForceRoute { get; init; }

    /// <summary>The catalog provider for a forced <see cref="AccountRoute.Imap"/> route; None runs IMAP discovery.</summary>
    public SpecialImapProvider ForcedImapProvider { get; init; }
}

public enum AccountCapabilitySupportKind
{
    /// <summary>Not available through the route; the mode picker offers "On this PC" or Off (or a typed DAV URL).</summary>
    None,

    /// <summary>The route's own API (Gmail, Outlook, Exchange) or, for Mail on the IMAP route, IMAP/SMTP.</summary>
    Provider,

    CalDav,
    CardDav
}

public sealed record AccountCapabilitySupport(AccountCapabilitySupportKind Kind, Uri? ServiceUri = null)
{
    public static AccountCapabilitySupport None { get; } = new(AccountCapabilitySupportKind.None);
    public static AccountCapabilitySupport Provider { get; } = new(AccountCapabilitySupportKind.Provider);

    public bool IsSupported => Kind != AccountCapabilitySupportKind.None;
}

public sealed record AccountRouteSupport(
    AccountCapabilitySupport Mail,
    AccountCapabilitySupport Calendar,
    AccountCapabilitySupport Contacts,
    AccountCapabilitySupport Tasks)
{
    public static AccountRouteSupport None { get; } = new(
        AccountCapabilitySupport.None, AccountCapabilitySupport.None, AccountCapabilitySupport.None, AccountCapabilitySupport.None);

    public static AccountRouteSupport AllProvider { get; } = new(
        AccountCapabilitySupport.Provider, AccountCapabilitySupport.Provider, AccountCapabilitySupport.Provider, AccountCapabilitySupport.Provider);
}

/// <summary>
/// The result of address-first resolution: the route, why it was chosen, what the route supports, and for the IMAP
/// route everything its sign-in options need. Nothing in it is persisted.
/// </summary>
public sealed class AccountRouteResolution
{
    public string Address { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public AccountRoute Route { get; init; }
    public AccountRouteEvidence Evidence { get; init; }

    /// <summary>The MX host behind <see cref="AccountRouteEvidence.Mx"/> or <see cref="AccountRouteEvidence.UserRealm"/>, when one was read.</summary>
    public string? MxHost { get; init; }

    /// <summary>The discovered IMAP host behind <see cref="AccountRouteEvidence.ImapHost"/>.</summary>
    public string? ImapHost { get; init; }

    /// <summary>The catalog provider's display name for the IMAP route ("Yandex Mail"); not translated.</summary>
    public string? ProviderDisplayName { get; init; }

    /// <summary>The tenant's federation brand name from the user-realm answer, when Microsoft returned one.</summary>
    public string? OrganizationName { get; init; }

    /// <summary>The MX host is a known mail filter and the user-realm request ran.</summary>
    public bool WasMicrosoftAsked { get; init; }

    public AccountRouteSupport Support { get; init; } = AccountRouteSupport.None;

    /// <summary>Set for <see cref="AccountRoute.Imap"/> only.</summary>
    public ImapSetupResolution? Imap { get; init; }

    /// <summary>Set for <see cref="AccountRoute.Exchange"/> only.</summary>
    public ExchangeRouteDetails? Exchange { get; init; }

    /// <summary>The user chose the provider (<see cref="AccountRouteRequest.ForceRoute"/>) instead of detection deciding it.</summary>
    public bool IsManual { get; init; }

    /// <summary>The hard deadline ended the resolution; <see cref="Route"/> is <see cref="AccountRoute.NotFound"/>.</summary>
    public bool IsTimedOut { get; init; }

    public TimeSpan Elapsed { get; init; }
}
