#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Services;

public sealed class ImapSetupResolver : IImapSetupResolver
{
    private static readonly TimeSpan SignInCheckTimeout = TimeSpan.FromSeconds(5);
    private static readonly ILogger Logger = Log.ForContext<ImapSetupResolver>();

    private static readonly HashSet<string> OAuthMechanisms = new(StringComparer.OrdinalIgnoreCase)
    {
        "OAUTHBEARER",
        "XOAUTH2"
    };

    private readonly IKnownImapProviderCatalog _catalog;
    private readonly IAutoDiscoveryService _autoDiscoveryService;
    private readonly IImapAuthCapabilityProbe _authCapabilityProbe;
    private readonly IOAuthIssuerDiscoveryService _issuerDiscoveryService;

    public ImapSetupResolver(
        IKnownImapProviderCatalog catalog,
        IAutoDiscoveryService autoDiscoveryService,
        IImapAuthCapabilityProbe authCapabilityProbe,
        IOAuthIssuerDiscoveryService issuerDiscoveryService)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(autoDiscoveryService);
        ArgumentNullException.ThrowIfNull(authCapabilityProbe);
        ArgumentNullException.ThrowIfNull(issuerDiscoveryService);

        _catalog = catalog;
        _autoDiscoveryService = autoDiscoveryService;
        _authCapabilityProbe = authCapabilityProbe;
        _issuerDiscoveryService = issuerDiscoveryService;
    }

    public async Task<ImapSetupResolution> ResolveAsync(
        ImapSetupRequest request,
        IProgress<ImapSetupStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var address = request.Address?.Trim() ?? string.Empty;
        var domain = GetDomain(address);
        if (domain.Length == 0)
            return ImapSetupResolution.NotFound(address, domain);

        var catalogProvider = _catalog.Match(address, string.Empty, request.PreferredProvider);
        if (catalogProvider is not null)
            return await ResolveCatalogProviderAsync(request, address, domain, catalogProvider, discovered: null, cancellationToken).ConfigureAwait(false);

        return await DiscoverAsync(request, address, domain, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImapSetupResolution> DiscoverAsync(
        ImapSetupRequest request,
        string address,
        string domain,
        IProgress<ImapSetupStage>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(ImapSetupStage.FindingServers);

        using var davCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var calDavTask = request.DiscoverCalendar
            ? DiscoverDavAsync(address, isCalendar: true, davCancellation.Token)
            : Task.FromResult<Uri?>(null);
        var cardDavTask = request.DiscoverContacts
            ? DiscoverDavAsync(address, isCalendar: false, davCancellation.Token)
            : Task.FromResult<Uri?>(null);

        try
        {
            var serverInformation = await DiscoverServersAsync(address, cancellationToken).ConfigureAwait(false);

            if (serverInformation is null)
            {
                var (missingMailCalDav, missingMailCardDav) = await AwaitDavAsync(calDavTask, cardDavTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                return new ImapSetupResolution
                {
                    Address = address,
                    Domain = domain,
                    Source = ImapSetupSource.None,
                    CalDavServiceUri = missingMailCalDav,
                    CardDavServiceUri = missingMailCardDav
                };
            }

            var incomingHost = serverInformation.IncomingServer?.Trim() ?? string.Empty;

            var nativeProvider = request.DetectsNativeProvider ? KnownMailHosts.GetProviderByImapHost(incomingHost) : null;
            if (nativeProvider is not null)
            {
                davCancellation.Cancel();

                return new ImapSetupResolution
                {
                    Address = address,
                    Domain = domain,
                    Source = ImapSetupSource.Discovery,
                    ServerInformation = serverInformation,
                    IncomingHost = incomingHost,
                    NativeProvider = nativeProvider
                };
            }

            var hostedProvider = _catalog.Match(address, incomingHost);
            if (hostedProvider is not null)
            {
                davCancellation.Cancel();
                return await ResolveCatalogProviderAsync(request, address, domain, hostedProvider, serverInformation, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(ImapSetupStage.CheckingSignIn);
            var capabilities = await ProbeAsync(serverInformation, cancellationToken).ConfigureAwait(false);

            var oauthDiscovery = OAuthDiscoveryResult.NotOffered;
            if (capabilities.SupportsOAuth)
            {
                progress?.Report(ImapSetupStage.LookingForSignInPage);
                oauthDiscovery = await DiscoverIssuerAsync(address, incomingHost, capabilities, cancellationToken).ConfigureAwait(false);
            }

            var (calDavUri, cardDavUri) = await AwaitDavAsync(calDavTask, cardDavTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new ImapSetupResolution
            {
                Address = address,
                Domain = domain,
                Source = ImapSetupSource.Discovery,
                ServerInformation = serverInformation,
                IncomingHost = incomingHost,
                AuthCapabilities = capabilities,
                OAuthDiscovery = oauthDiscovery,
                CalDavServiceUri = calDavUri,
                CardDavServiceUri = cardDavUri,
                Options = BuildDiscoveredOptions(request, address, capabilities, oauthDiscovery)
            };
        }
        finally
        {
            // Background DAV probes must not outlive a resolution that finished, failed or was cancelled.
            davCancellation.Cancel();
            await Task.WhenAll(calDavTask, cardDavTask).ConfigureAwait(false);
        }
    }

    private async Task<ImapSetupResolution> ResolveCatalogProviderAsync(
        ImapSetupRequest request,
        string address,
        string domain,
        KnownImapProviderDefinition provider,
        CustomServerInformation? discovered,
        CancellationToken cancellationToken)
    {
        var regions = provider.Regions ?? Array.Empty<KnownImapProviderRegion>();
        var defaultRegion = SelectDefaultRegion(regions, discovered?.IncomingServer, domain);
        var incomingHost = discovered?.IncomingServer?.Trim()
                           ?? NullIfEmpty(defaultRegion?.IncomingHost)
                           ?? provider.Incoming.Host;

        var oauthDiscovery = OAuthDiscoveryResult.NotOffered;
        if (request.AllowBrowserSignIn && !string.IsNullOrWhiteSpace(provider.OAuth?.ClientId))
        {
            // A catalog registration resolves from the catalog itself, without a network call.
            oauthDiscovery = await DiscoverIssuerAsync(address, incomingHost, ImapAuthCapabilities.Unknown, cancellationToken).ConfigureAwait(false);
        }

        var calDavUrl = NullIfEmpty(defaultRegion?.CalDavServiceUrl) ?? NullIfEmpty(provider.CalDavServiceUrl);
        var cardDavUrl = NullIfEmpty(defaultRegion?.CardDavServiceUrl) ?? NullIfEmpty(provider.CardDavServiceUrl);
        var requiresLoginAddress = _catalog.RequiresLoginAddress(provider, address);

        return new ImapSetupResolution
        {
            Address = address,
            Domain = domain,
            Source = discovered is null ? ImapSetupSource.Catalog : ImapSetupSource.DiscoveryCatalog,
            Provider = provider,
            ServerInformation = discovered,
            IncomingHost = incomingHost,
            OAuthDiscovery = oauthDiscovery,
            CalDavServiceUri = TryCreateUri(calDavUrl),
            CardDavServiceUri = TryCreateUri(cardDavUrl),
            Regions = regions,
            DefaultRegion = defaultRegion,
            RequiresLoginAddress = requiresLoginAddress,
            Options = BuildCatalogOptions(provider, oauthDiscovery, regions.Count > 0, requiresLoginAddress)
        };
    }

    /// <summary>
    /// Browser first, then one password option. The catalog's password kind decides which: an app-password provider
    /// (setup hint AppPasswordRequired and the like) never offers its rejected account password.
    /// </summary>
    private static ImapSignInOption[] BuildCatalogOptions(
        KnownImapProviderDefinition provider,
        OAuthDiscoveryResult oauthDiscovery,
        bool hasRegions,
        bool requiresLoginAddress)
    {
        var options = new List<ImapSignInOption>();

        if (oauthDiscovery.CanSignIn)
            options.Add(new ImapSignInOption { Kind = ImapSignInOptionKind.Browser, OAuthDiscovery = oauthDiscovery });

        var isAppPassword = provider.PasswordKind is KnownImapPasswordKind.AppPassword or KnownImapPasswordKind.BridgePassword;

        options.Add(new ImapSignInOption
        {
            Kind = isAppPassword ? ImapSignInOptionKind.AppPassword : ImapSignInOptionKind.AccountPassword,
            PasswordKind = provider.PasswordKind,
            SetupHint = provider.SetupHint,
            IsSetupHintBlocking = IsBlocking(provider.SetupHint),
            HelpUrl = NullIfEmpty(provider.AppPasswordHelpUrl),
            ShowsRegions = hasRegions,
            RequiresLoginAddress = requiresLoginAddress
        });

        return options.ToArray();
    }

    /// <summary>
    /// A discovered server offers the browser when an issuer was found, and a password unless the server advertised
    /// OAuth mechanisms only. Both appear when both work, browser first. A domain with catalog app-password guidance
    /// gets the app-password card instead of the plain password.
    /// </summary>
    private ImapSignInOption[] BuildDiscoveredOptions(
        ImapSetupRequest request,
        string address,
        ImapAuthCapabilities capabilities,
        OAuthDiscoveryResult oauthDiscovery)
    {
        var options = new List<ImapSignInOption>();

        if (request.AllowBrowserSignIn && oauthDiscovery.CanSignIn)
            options.Add(new ImapSignInOption { Kind = ImapSignInOptionKind.Browser, OAuthDiscovery = oauthDiscovery });

        if (!AcceptsPassword(capabilities))
            return options.ToArray();

        var appPasswordHelp = _catalog.FindAppPasswordHelp(address);

        options.Add(appPasswordHelp is null
            ? new ImapSignInOption { Kind = ImapSignInOptionKind.AccountPassword }
            : new ImapSignInOption
            {
                Kind = ImapSignInOptionKind.AppPassword,
                PasswordKind = KnownImapPasswordKind.AppPassword,
                HelpUrl = NullIfEmpty(appPasswordHelp.HelpUrl)
            });

        return options.ToArray();
    }

    /// <summary>An unreachable server or an empty mechanism list cannot rule out LOGIN, so both count as accepting a password.</summary>
    private static bool AcceptsPassword(ImapAuthCapabilities capabilities)
        => !capabilities.Reached
           || capabilities.Mechanisms.Count == 0
           || capabilities.Mechanisms.Any(mechanism => !OAuthMechanisms.Contains(mechanism));

    private static bool IsBlocking(KnownImapSetupHint hint)
        => hint is KnownImapSetupHint.ImapDisabledByDefault
            or KnownImapSetupHint.LocalBridgeRequired
            or KnownImapSetupHint.ThirdPartyAccessRequired;

    /// <summary>
    /// The region whose IMAP host was discovered, else the one whose host shares the address's public suffix
    /// (zohomail.eu picks imap.zoho.eu), else the catalog's first.
    /// </summary>
    internal static KnownImapProviderRegion? SelectDefaultRegion(IReadOnlyList<KnownImapProviderRegion> regions, string? discoveredHost, string domain)
    {
        if (regions.Count == 0)
            return null;

        var host = discoveredHost?.Trim().TrimEnd('.');
        if (!string.IsNullOrEmpty(host))
        {
            var byHost = regions.FirstOrDefault(region => string.Equals(region.IncomingHost, host, StringComparison.OrdinalIgnoreCase));
            if (byHost is not null)
                return byHost;
        }

        var suffix = GetPublicSuffix(domain);
        return regions.FirstOrDefault(region => !string.IsNullOrWhiteSpace(region.IncomingHost)
                                                && string.Equals(GetPublicSuffix(region.IncomingHost), suffix, StringComparison.OrdinalIgnoreCase))
               ?? regions[0];
    }

    private static string GetPublicSuffix(string host)
    {
        var registrable = RegistrableDomain.Get(host);
        var separator = registrable.IndexOf('.');
        return separator < 0 ? registrable : registrable[(separator + 1)..];
    }

    private async Task<CustomServerInformation?> DiscoverServersAsync(string address, CancellationToken cancellationToken)
    {
        var minimalSettings = new AutoDiscoveryMinimalSettings
        {
            Email = address,
            DisplayName = string.Empty,
            Password = string.Empty
        };

        try
        {
            var settings = await _autoDiscoveryService.GetAutoDiscoverySettings(minimalSettings, cancellationToken).ConfigureAwait(false);
            if (settings is null)
                return null;

            settings.UserMinimalSettings = minimalSettings;
            return settings.ToServerInformation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("Server discovery for an IMAP setup ended without settings ({ErrorType}).", ex.GetType().Name);
            return null;
        }
    }

    private async Task<Uri?> DiscoverDavAsync(string address, bool isCalendar, CancellationToken cancellationToken)
    {
        try
        {
            return isCalendar
                ? await _autoDiscoveryService.DiscoverCalDavServiceUriAsync(address, cancellationToken).ConfigureAwait(false)
                : await _autoDiscoveryService.DiscoverCardDavServiceUriAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Information("{Service} discovery for an IMAP setup failed ({ErrorType}).", isCalendar ? "CalDAV" : "CardDAV", ex.GetType().Name);
            return null;
        }
    }

    private static async Task<(Uri? CalDav, Uri? CardDav)> AwaitDavAsync(Task<Uri?> calDavTask, Task<Uri?> cardDavTask)
    {
        var calDav = await calDavTask.ConfigureAwait(false);
        var cardDav = await cardDavTask.ConfigureAwait(false);
        return (calDav, cardDav);
    }

    private async Task<ImapAuthCapabilities> ProbeAsync(CustomServerInformation serverInformation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInCheckTimeout);

        try
        {
            return await _authCapabilityProbe.ProbeAsync(serverInformation, timeout.Token).ConfigureAwait(false)
                   ?? ImapAuthCapabilities.Unknown;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("The IMAP capability probe for setup ended with {Reason}.", ex.GetType().Name);
            return ImapAuthCapabilities.Unknown;
        }
    }

    private async Task<OAuthDiscoveryResult> DiscoverIssuerAsync(string address, string imapHost, ImapAuthCapabilities capabilities, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInCheckTimeout);

        try
        {
            return await _issuerDiscoveryService
                       .DiscoverAsync(address, imapHost, capabilities, timeout.Token, deferClientRegistration: true)
                       .ConfigureAwait(false)
                   ?? OAuthDiscoveryResult.NotOffered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("OAuth issuer discovery for setup ended with {Reason}.", ex.GetType().Name);
            return OAuthDiscoveryResult.NotOffered;
        }
    }

    private static Uri? TryCreateUri(string? value)
        => !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? uri : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetDomain(string address)
    {
        var separator = address.LastIndexOf('@');
        return separator > 0 && separator < address.Length - 1 ? address[(separator + 1)..].Trim().ToLowerInvariant() : string.Empty;
    }
}
