#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Services;

public sealed class AccountRouteResolver : IAccountRouteResolver
{
    private static readonly ILogger Logger = Log.ForContext<AccountRouteResolver>();

    private readonly IKnownImapProviderCatalog _catalog;
    private readonly IDnsLookupService _dnsLookupService;
    private readonly IMicrosoftUserRealmService _userRealmService;
    private readonly IImapSetupResolver _imapSetupResolver;
    private readonly IExchangeAutoDiscoveryService? _exchangeAutoDiscoveryService;
    private readonly IExchangeAuthCapabilityProbe? _exchangeAuthCapabilityProbe;

    public AccountRouteResolver(
        IKnownImapProviderCatalog catalog,
        IDnsLookupService dnsLookupService,
        IMicrosoftUserRealmService userRealmService,
        IImapSetupResolver imapSetupResolver,
        IExchangeAutoDiscoveryService? exchangeAutoDiscoveryService = null,
        IExchangeAuthCapabilityProbe? exchangeAuthCapabilityProbe = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dnsLookupService);
        ArgumentNullException.ThrowIfNull(userRealmService);
        ArgumentNullException.ThrowIfNull(imapSetupResolver);

        _catalog = catalog;
        _dnsLookupService = dnsLookupService;
        _userRealmService = userRealmService;
        _imapSetupResolver = imapSetupResolver;
        _exchangeAutoDiscoveryService = exchangeAutoDiscoveryService;
        _exchangeAuthCapabilityProbe = exchangeAuthCapabilityProbe;
    }

    public async Task<AccountRouteResolution> ResolveAsync(
        AccountRouteRequest request,
        IProgress<AccountRouteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var address = request.Address?.Trim() ?? string.Empty;
        var domain = GetDomain(address);

        if (domain.Length == 0)
            return NotFound(address, domain, stopwatch, isTimedOut: false);

        var imapRequest = new ImapSetupRequest
        {
            Address = address,
            DiscoverCalendar = request.DiscoverCalendar,
            DiscoverContacts = request.DiscoverContacts,
            AllowBrowserSignIn = request.AllowBrowserSignIn
        };

        if (request.ForceRoute is { } forcedRoute)
            return await ResolveForcedAsync(request, forcedRoute, address, domain, imapRequest, progress, stopwatch, cancellationToken).ConfigureAwait(false);

        var consumerProvider = KnownMailHosts.GetConsumerProvider(domain);
        if (consumerProvider is { } consumer)
        {
            return Native(address, domain, consumer, AccountRouteEvidence.Domain, stopwatch);
        }

        if (_catalog.Match(address, string.Empty) is not null)
        {
            var catalogResolution = await _imapSetupResolver.ResolveAsync(imapRequest, null, cancellationToken).ConfigureAwait(false);
            return Imap(catalogResolution, AccountRouteEvidence.Catalog, stopwatch);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AccountRouteDeadlines.Hard);

        using var reporter = new StageReporter(progress, domain);

        try
        {
            return await ResolveFromNetworkAsync(address, domain, imapRequest, reporter, stopwatch, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Information("Account route resolution for {Domain} reached the {Seconds} s deadline.", domain, AccountRouteDeadlines.Hard.TotalSeconds);
            return NotFound(address, domain, stopwatch, isTimedOut: true);
        }
    }

    /// <summary>
    /// The user's provider choice. Gmail, Outlook and a catalog provider need no network; Other (IMAP) runs only IMAP
    /// discovery and keeps a Gmail or Outlook host on IMAP; Exchange runs only Exchange Autodiscover, and a Microsoft 365
    /// answer still becomes the Outlook route because Exchange Online does not accept a password.
    /// </summary>
    private async Task<AccountRouteResolution> ResolveForcedAsync(
        AccountRouteRequest request,
        AccountRoute forcedRoute,
        string address,
        string domain,
        ImapSetupRequest imapRequest,
        IProgress<AccountRouteProgress>? progress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        switch (forcedRoute)
        {
            case AccountRoute.Gmail:
                return Native(address, domain, MailProviderType.Gmail, AccountRouteEvidence.None, stopwatch, isManual: true);
            case AccountRoute.Outlook:
                return Native(address, domain, MailProviderType.Outlook, AccountRouteEvidence.None, stopwatch, isManual: true);
            case AccountRoute.Imap when request.ForcedImapProvider != SpecialImapProvider.None:
                var catalogResolution = await _imapSetupResolver.ResolveAsync(
                    imapRequest with { PreferredProvider = request.ForcedImapProvider },
                    null,
                    cancellationToken).ConfigureAwait(false);
                return Imap(catalogResolution, AccountRouteEvidence.None, stopwatch, isManual: true);
            case AccountRoute.Imap or AccountRoute.Exchange:
                break;
            default:
                return NotFound(address, domain, stopwatch, isTimedOut: false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(AccountRouteDeadlines.Hard);

        using var reporter = new StageReporter(progress, domain);

        try
        {
            if (forcedRoute == AccountRoute.Exchange)
            {
                reporter.Report(AccountRouteResolveStage.LookingForExchange);

                var exchange = await DiscoverExchangeAsync(address, deadline.Token).ConfigureAwait(false);
                if (exchange is null)
                    return NotFound(address, domain, stopwatch, isTimedOut: false);

                return Exchange(address, domain, exchange, stopwatch, mxHost: null, wasMicrosoftAsked: false, isManual: !exchange.IsMicrosoft365);
            }

            var imapProgress = new SynchronousProgress<ImapSetupStage>(stage => reporter.Report(stage switch
            {
                ImapSetupStage.CheckingSignIn => AccountRouteResolveStage.CheckingSignIn,
                ImapSetupStage.LookingForSignInPage => AccountRouteResolveStage.LookingForSignInPage,
                _ => AccountRouteResolveStage.FindingServers
            }));

            var imap = await _imapSetupResolver.ResolveAsync(
                imapRequest with { DetectsNativeProvider = false },
                imapProgress,
                deadline.Token).ConfigureAwait(false);

            return Imap(imap, AccountRouteEvidence.ImapHost, stopwatch, isManual: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Information("Forced {Route} resolution for {Domain} reached the {Seconds} s deadline.", forcedRoute, domain, AccountRouteDeadlines.Hard.TotalSeconds);
            return NotFound(address, domain, stopwatch, isTimedOut: true);
        }
    }

    private async Task<AccountRouteResolution> ResolveFromNetworkAsync(
        string address,
        string domain,
        ImapSetupRequest imapRequest,
        StageReporter reporter,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        reporter.Report(AccountRouteResolveStage.CheckingDomainHost);
        var mxRecords = await _dnsLookupService.GetMxRecordsAsync(domain, cancellationToken).ConfigureAwait(false);

        foreach (var mxRecord in mxRecords)
        {
            if (KnownMailHosts.GetHostedMailProviderByMx(mxRecord.Host) is { } hostedProvider)
                return Native(address, domain, hostedProvider, AccountRouteEvidence.Mx, stopwatch, mxHost: mxRecord.Host);
        }

        var filterHost = mxRecords.FirstOrDefault(record => KnownMailHosts.IsMailFilterHost(record.Host))?.Host;
        if (filterHost is not null)
        {
            reporter.Report(AccountRouteResolveStage.AskingMicrosoft);
            var realm = await _userRealmService.GetUserRealmAsync(address, cancellationToken).ConfigureAwait(false);

            if (realm.IsMicrosoft365)
            {
                return Native(address, domain, MailProviderType.Outlook, AccountRouteEvidence.UserRealm, stopwatch,
                    mxHost: filterHost, organizationName: realm.FederationBrandName, wasMicrosoftAsked: true);
            }
        }

        var imapProgress = new SynchronousProgress<ImapSetupStage>(stage => reporter.Report(stage switch
        {
            ImapSetupStage.CheckingSignIn => AccountRouteResolveStage.CheckingSignIn,
            ImapSetupStage.LookingForSignInPage => AccountRouteResolveStage.LookingForSignInPage,
            _ => AccountRouteResolveStage.FindingServers
        }));

        var primaryMxHost = mxRecords.FirstOrDefault()?.Host;
        var wasMicrosoftAsked = filterHost is not null;

        // Exchange Autodiscover starts only once the domain table, the catalog, MX and the user realm have not decided,
        // and runs beside the IMAP discovery: an IMAP answer is never held for it, and it decides only when it
        // answered first or IMAP found nothing.
        using var exchangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exchangeTask = DiscoverExchangeAsync(address, exchangeCancellation.Token);

        ImapSetupResolution imap;
        try
        {
            imap = await _imapSetupResolver.ResolveAsync(imapRequest, imapProgress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            exchangeCancellation.Cancel();
            throw;
        }

        if (exchangeTask.IsCompletedSuccessfully && exchangeTask.Result is { } earlyExchange)
            return Exchange(address, domain, earlyExchange, stopwatch, primaryMxHost, wasMicrosoftAsked);

        if (imap.NativeProvider is { } nativeProvider)
        {
            exchangeCancellation.Cancel();
            return Native(address, domain, nativeProvider, AccountRouteEvidence.ImapHost, stopwatch,
                mxHost: primaryMxHost, imapHost: imap.IncomingHost, wasMicrosoftAsked: wasMicrosoftAsked);
        }

        if (imap.IsFound)
        {
            exchangeCancellation.Cancel();
            return Imap(imap, AccountRouteEvidence.ImapHost, stopwatch, primaryMxHost, wasMicrosoftAsked: wasMicrosoftAsked);
        }

        if (!exchangeTask.IsCompleted)
            reporter.Report(AccountRouteResolveStage.LookingForExchange);

        if (await exchangeTask.ConfigureAwait(false) is { } exchange)
            return Exchange(address, domain, exchange, stopwatch, primaryMxHost, wasMicrosoftAsked);

        return NotFound(address, domain, stopwatch, isTimedOut: false, wasMicrosoftAsked: wasMicrosoftAsked);
    }

    /// <summary>
    /// The mailbox's Exchange endpoint from Autodiscover and, for an on-premises server, how it accepts sign-in. Null
    /// when Autodiscover finds nothing or fails; only cancellation throws.
    /// </summary>
    private async Task<ExchangeDiscovery?> DiscoverExchangeAsync(string address, CancellationToken cancellationToken)
    {
        if (_exchangeAutoDiscoveryService is null)
            return null;

        try
        {
            var ewsUrl = await _exchangeAutoDiscoveryService.TryDiscoverEwsUrlAsync(address, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ewsUrl) || !Uri.TryCreate(ewsUrl, UriKind.Absolute, out var ewsUri))
                return null;

            if (KnownMailHosts.IsExchangeOnlineHost(ewsUri.Host))
                return new ExchangeDiscovery(ewsUrl, IsMicrosoft365: true, ExchangeAuthProbeResult.Unknown);

            var probe = _exchangeAuthCapabilityProbe is null
                ? ExchangeAuthProbeResult.Unknown
                : await _exchangeAuthCapabilityProbe.ProbeAsync(ewsUrl, address, cancellationToken).ConfigureAwait(false);

            return new ExchangeDiscovery(ewsUrl, IsMicrosoft365: false, probe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("Exchange Autodiscover during account route resolution failed ({ErrorType}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Microsoft 365 keeps the Outlook route; only an on-premises server becomes the Exchange route.</summary>
    private static AccountRouteResolution Exchange(
        string address,
        string domain,
        ExchangeDiscovery discovery,
        Stopwatch stopwatch,
        string? mxHost,
        bool wasMicrosoftAsked,
        bool isManual = false)
    {
        if (discovery.IsMicrosoft365)
        {
            return Native(address, domain, MailProviderType.Outlook, AccountRouteEvidence.ExchangeAutodiscover, stopwatch,
                mxHost: mxHost, wasMicrosoftAsked: wasMicrosoftAsked);
        }

        return new AccountRouteResolution
        {
            Address = address,
            Domain = domain,
            Route = AccountRoute.Exchange,
            Evidence = AccountRouteEvidence.ExchangeAutodiscover,
            MxHost = mxHost,
            WasMicrosoftAsked = wasMicrosoftAsked,
            Support = AccountRouteSupport.AllProvider,
            Exchange = new ExchangeRouteDetails(discovery.EwsUrl, discovery.AuthProbe),
            IsManual = isManual,
            Elapsed = stopwatch.Elapsed
        };
    }

    private sealed record ExchangeDiscovery(string EwsUrl, bool IsMicrosoft365, ExchangeAuthProbeResult AuthProbe);

    private static AccountRouteResolution Native(
        string address,
        string domain,
        MailProviderType provider,
        AccountRouteEvidence evidence,
        Stopwatch stopwatch,
        string? mxHost = null,
        string? imapHost = null,
        string? organizationName = null,
        bool wasMicrosoftAsked = false,
        bool isManual = false) => new()
        {
            Address = address,
            Domain = domain,
            Route = provider == MailProviderType.Gmail ? AccountRoute.Gmail : AccountRoute.Outlook,
            Evidence = evidence,
            MxHost = mxHost,
            ImapHost = imapHost,
            OrganizationName = organizationName,
            WasMicrosoftAsked = wasMicrosoftAsked,
            Support = AccountRouteSupport.AllProvider,
            IsManual = isManual,
            Elapsed = stopwatch.Elapsed
        };

    private static AccountRouteResolution Imap(
        ImapSetupResolution imap,
        AccountRouteEvidence evidence,
        Stopwatch stopwatch,
        string? mxHost = null,
        bool wasMicrosoftAsked = false,
        bool isManual = false) => new()
        {
            Address = imap.Address,
            Domain = imap.Domain,
            Route = imap.IsFound ? AccountRoute.Imap : AccountRoute.NotFound,
            Evidence = imap.IsFound ? evidence : AccountRouteEvidence.None,
            MxHost = mxHost,
            ImapHost = imap.IncomingHost,
            ProviderDisplayName = imap.Provider?.DisplayName,
            WasMicrosoftAsked = wasMicrosoftAsked,
            Support = imap.IsFound ? GetImapSupport(imap) : AccountRouteSupport.None,
            Imap = imap.IsFound ? imap : null,
            IsManual = imap.IsFound && isManual,
            Elapsed = stopwatch.Elapsed
        };

    private static AccountRouteResolution NotFound(string address, string domain, Stopwatch stopwatch, bool isTimedOut, bool wasMicrosoftAsked = false) => new()
    {
        Address = address,
        Domain = domain,
        Route = AccountRoute.NotFound,
        Evidence = AccountRouteEvidence.None,
        WasMicrosoftAsked = wasMicrosoftAsked,
        IsTimedOut = isTimedOut,
        Elapsed = stopwatch.Elapsed
    };

    private static AccountRouteSupport GetImapSupport(ImapSetupResolution imap) => new(
        AccountCapabilitySupport.Provider,
        imap.CalDavServiceUri is null ? AccountCapabilitySupport.None : new AccountCapabilitySupport(AccountCapabilitySupportKind.CalDav, imap.CalDavServiceUri),
        imap.CardDavServiceUri is null ? AccountCapabilitySupport.None : new AccountCapabilitySupport(AccountCapabilitySupportKind.CardDav, imap.CardDavServiceUri),
        AccountCapabilitySupport.None);

    private static string GetDomain(string address)
    {
        var separator = address.LastIndexOf('@');
        return separator > 0 && separator < address.Length - 1 ? address[(separator + 1)..].Trim().ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// Reports each stage once and, when the soft deadline passes, the running stage again marked slow.
    /// </summary>
    private sealed class StageReporter : IDisposable
    {
        private readonly IProgress<AccountRouteProgress>? _progress;
        private readonly string _domain;
        private readonly Timer? _slowTimer;
        private readonly object _lock = new();
        private AccountRouteResolveStage? _currentStage;
        private bool _isSlow;
        private bool _isDisposed;

        public StageReporter(IProgress<AccountRouteProgress>? progress, string domain)
        {
            _progress = progress;
            _domain = domain;

            if (progress is not null)
                _slowTimer = new Timer(_ => MarkSlow(), null, AccountRouteDeadlines.Soft, Timeout.InfiniteTimeSpan);
        }

        public void Report(AccountRouteResolveStage stage)
        {
            bool isSlow;

            lock (_lock)
            {
                if (_isDisposed || _currentStage == stage)
                    return;

                _currentStage = stage;
                isSlow = _isSlow;
            }

            _progress?.Report(new AccountRouteProgress(stage, _domain, isSlow));
        }

        private void MarkSlow()
        {
            AccountRouteResolveStage stage;

            lock (_lock)
            {
                if (_isDisposed || _currentStage is not { } current)
                    return;

                _isSlow = true;
                stage = current;
            }

            _progress?.Report(new AccountRouteProgress(stage, _domain, IsSlow: true));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _isDisposed = true;
            }

            _slowTimer?.Dispose();
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
