#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Models.Accounts;

public sealed record ImapSetupRequest
{
    public string Address { get; init; } = string.Empty;

    /// <summary>Look for a CalDAV service when the catalog does not name one.</summary>
    public bool DiscoverCalendar { get; init; } = true;

    /// <summary>Look for a CardDAV service when the catalog does not name one.</summary>
    public bool DiscoverContacts { get; init; } = true;

    /// <summary>False on heads without the secure sign-in surface; the browser option is then never offered.</summary>
    public bool AllowBrowserSignIn { get; init; } = true;
}

public enum ImapSetupStage
{
    FindingServers,
    CheckingSignIn,
    LookingForSignInPage
}

public enum ImapSetupSource
{
    /// <summary>Nothing was found for the address.</summary>
    None,

    /// <summary>The address domain is a catalog provider's; no network call ran.</summary>
    Catalog,

    /// <summary>Autodiscovery found the servers, and they belong to a catalog provider (a custom domain on iCloud, IONOS...).</summary>
    DiscoveryCatalog,

    /// <summary>Autodiscovery found the servers.</summary>
    Discovery
}

public enum ImapSignInOptionKind
{
    /// <summary>Sign in via browser (OAuth). Always first when offered.</summary>
    Browser,

    /// <summary>An app-specific password, or a bridge password when <see cref="ImapSignInOption.PasswordKind"/> says so.</summary>
    AppPassword,

    /// <summary>The account's own IMAP password.</summary>
    AccountPassword
}

/// <summary>One sign-in choice for the IMAP route with everything its card shows.</summary>
public sealed record ImapSignInOption
{
    public ImapSignInOptionKind Kind { get; init; }

    /// <summary>AppPassword or BridgePassword for <see cref="ImapSignInOptionKind.AppPassword"/>; AccountPassword otherwise.</summary>
    public KnownImapPasswordKind PasswordKind { get; init; } = KnownImapPasswordKind.AccountPassword;

    /// <summary>The provider's setup hint for password options. None for the browser option.</summary>
    public KnownImapSetupHint SetupHint { get; init; }

    /// <summary>The hint blocks sign-in until the user changes something at the provider (warning style).</summary>
    public bool IsSetupHintBlocking { get; init; }

    /// <summary>"How to create an app password" or the provider's setup help. Null when there is none.</summary>
    public string? HelpUrl { get; init; }

    /// <summary>The password card shows the region choice from <see cref="ImapSetupResolution.Regions"/>.</summary>
    public bool ShowsRegions { get; init; }

    /// <summary>The password card asks for the provider login address (custom domain on a <c>CustomDomainLogin</c> provider).</summary>
    public bool RequiresLoginAddress { get; init; }

    /// <summary>For <see cref="ImapSignInOptionKind.Browser"/>: the issuer, profile and confirmation rule. Null otherwise.</summary>
    public OAuthDiscoveryResult? OAuthDiscovery { get; init; }
}

/// <summary>
/// What the IMAP route needs for its sign-in step. Nothing in it is persisted, and it carries no password.
/// </summary>
public sealed class ImapSetupResolution
{
    public string Address { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public ImapSetupSource Source { get; init; }

    /// <summary>The catalog provider, matched by address domain or by the discovered IMAP host. Null for unknown servers.</summary>
    public KnownImapProviderDefinition? Provider { get; init; }

    /// <summary>
    /// The discovered servers (no password). Null when the catalog matched the address domain: a catalog provider's
    /// servers come from <see cref="Interfaces.ISpecialImapProviderConfigResolver"/> with the chosen region and login address.
    /// </summary>
    public CustomServerInformation? ServerInformation { get; init; }

    /// <summary>The IMAP host the options refer to: the discovered host or the catalog provider's (default region) host.</summary>
    public string? IncomingHost { get; init; }

    /// <summary>Gmail or Outlook when the discovered IMAP host belongs to them; the route then leaves IMAP.</summary>
    public MailProviderType? NativeProvider { get; init; }

    public ImapAuthCapabilities AuthCapabilities { get; init; } = ImapAuthCapabilities.Unknown;
    public OAuthDiscoveryResult OAuthDiscovery { get; init; } = OAuthDiscoveryResult.NotOffered;

    public Uri? CalDavServiceUri { get; init; }
    public Uri? CardDavServiceUri { get; init; }

    /// <summary>The sign-in options in display order: browser, app password, account password. Unsupported ones are left out.</summary>
    public IReadOnlyList<ImapSignInOption> Options { get; init; } = Array.Empty<ImapSignInOption>();

    public IReadOnlyList<KnownImapProviderRegion> Regions { get; init; } = Array.Empty<KnownImapProviderRegion>();

    /// <summary>The region preselected from the discovered host or the address domain; null without regions.</summary>
    public KnownImapProviderRegion? DefaultRegion { get; init; }

    /// <summary>The address is on a custom domain and the provider signs in with an address on its own domains.</summary>
    public bool RequiresLoginAddress { get; init; }

    public bool IsFound => Provider is not null || ServerInformation is not null;

    public static ImapSetupResolution NotFound(string address, string domain) => new()
    {
        Address = address,
        Domain = domain,
        Source = ImapSetupSource.None
    };
}
