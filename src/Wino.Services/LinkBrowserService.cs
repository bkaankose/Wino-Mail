#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Browsers;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Services;

public sealed class LinkBrowserService : ILinkBrowserService
{
    // Stored in device-local configuration instead of IPreferencesService, whose properties all sync
    // through Wino Account and would carry another machine's browser ids here.
    private const string AllAccountsBrowserIdKey = "LinkBrowserId";
    private const string AllAccountsBrowserProfileIdKey = "LinkBrowserProfileId";
    private const string AllAccountsBrowserNameKey = "LinkBrowserName";

    private readonly IInstalledBrowserService _installedBrowserService;
    private readonly IExternalLauncher _externalLauncher;
    private readonly IConfigurationService _configurationService;
    private readonly IAccountService _accountService;
    private readonly IDialogServiceBase _dialogService;

    public LinkBrowserService(IInstalledBrowserService installedBrowserService,
                              IExternalLauncher externalLauncher,
                              IConfigurationService configurationService,
                              IAccountService accountService,
                              IDialogServiceBase dialogService)
    {
        _installedBrowserService = installedBrowserService;
        _externalLauncher = externalLauncher;
        _configurationService = configurationService;
        _accountService = accountService;
        _dialogService = dialogService;
    }

    public bool IsBrowserSelectionSupported => _installedBrowserService.IsSupported;

    public BrowserOption? AllAccountsBrowser
    {
        get
        {
            var browserId = _configurationService.Get(AllAccountsBrowserIdKey, string.Empty);
            if (string.IsNullOrEmpty(browserId)) return null;

            var profileId = _configurationService.Get(AllAccountsBrowserProfileIdKey, string.Empty);
            var name = _configurationService.Get(AllAccountsBrowserNameKey, string.Empty);

            return new BrowserOption(browserId, string.IsNullOrEmpty(profileId) ? null : profileId, string.IsNullOrEmpty(name) ? browserId : name);
        }
        set
        {
            _configurationService.Set(AllAccountsBrowserIdKey, value?.BrowserId ?? string.Empty);
            _configurationService.Set(AllAccountsBrowserProfileIdKey, value?.ProfileId ?? string.Empty);
            _configurationService.Set(AllAccountsBrowserNameKey, value?.DisplayName ?? string.Empty);
        }
    }

    public BrowserOption? GetCustomBrowser(MailAccountPreferences preferences)
        => string.IsNullOrEmpty(preferences.CustomLinkBrowserId)
            ? null
            : new BrowserOption(preferences.CustomLinkBrowserId, preferences.CustomLinkBrowserProfileId, preferences.CustomLinkBrowserName ?? preferences.CustomLinkBrowserId);

    public void SetCustomBrowser(MailAccountPreferences preferences, BrowserOption? browser)
    {
        var allAccountsBrowser = AllAccountsBrowser;
        var matchesAllAccounts = browser is null
            ? allAccountsBrowser is null
            : allAccountsBrowser is not null && browser.IsSameTarget(allAccountsBrowser);

        preferences.HasCustomLinkBrowser = !matchesAllAccounts;
        preferences.CustomLinkBrowserId = matchesAllAccounts ? null : browser?.BrowserId;
        preferences.CustomLinkBrowserProfileId = matchesAllAccounts ? null : browser?.ProfileId;
        preferences.CustomLinkBrowserName = matchesAllAccounts ? null : browser?.DisplayName;
    }

    public async Task<BrowserCatalog> GetBrowsersAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (!catalog.IsComplete) return catalog;

        try
        {
            if (AllAccountsBrowser is { } allAccountsBrowser && !catalog.Contains(allAccountsBrowser))
                ResetAllAccountsBrowser(allAccountsBrowser);

            foreach (var account in await _accountService.GetAccountsAsync().ConfigureAwait(false))
            {
                if (account.Preferences is not { HasCustomLinkBrowser: true } preferences) continue;
                if (GetCustomBrowser(preferences) is not { } browser || catalog.Contains(browser)) continue;

                await ResetAccountBrowserAsync(preferences, browser, account.Name).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not reset browsers that are no longer installed.");
        }

        return catalog;
    }

    public async Task<PlatformOperationResult> OpenLinkAsync(Uri uri, Guid? accountId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await TryOpenInSelectedBrowserAsync(uri, accountId, cancellationToken).ConfigureAwait(false))
                return new PlatformOperationResult(PlatformOperationStatus.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PlatformOperationResult(PlatformOperationStatus.Cancelled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening a link in the selected browser failed. Falling back to the system default.");
        }

        return await _externalLauncher.LaunchUriAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryOpenInSelectedBrowserAsync(Uri uri, Guid? accountId, CancellationToken cancellationToken)
    {
        if (!_installedBrowserService.IsSupported || !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var (browser, preferences, accountName) = await ResolveBrowserAsync(accountId).ConfigureAwait(false);
        if (browser is null) return false;

        var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);

        if (catalog.IsComplete && !catalog.Contains(browser))
        {
            if (preferences is null)
                ResetAllAccountsBrowser(browser);
            else
                await ResetAccountBrowserAsync(preferences, browser, accountName ?? string.Empty).ConfigureAwait(false);

            return false;
        }

        if (await _installedBrowserService.TryLaunchAsync(browser, uri, cancellationToken).ConfigureAwait(false))
            return true;

        Log.Warning("The selected browser did not open the link. Falling back to the system default.");
        return false;
    }

    /// <summary>The preferences are returned only when the browser comes from the account's own override.</summary>
    private async Task<(BrowserOption? Browser, MailAccountPreferences? Preferences, string? AccountName)> ResolveBrowserAsync(Guid? accountId)
    {
        if (accountId is Guid id && await _accountService.GetAccountAsync(id).ConfigureAwait(false) is { Preferences: { HasCustomLinkBrowser: true } preferences } account)
            return (GetCustomBrowser(preferences), preferences, account.Name);

        return (AllAccountsBrowser, null, null);
    }

    private async Task<BrowserCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        if (!_installedBrowserService.IsSupported) return BrowserCatalog.Unavailable;

        try
        {
            return await _installedBrowserService.GetBrowsersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing installed browsers failed.");
            return BrowserCatalog.Unavailable;
        }
    }

    private void ResetAllAccountsBrowser(BrowserOption staleBrowser)
    {
        AllAccountsBrowser = null;
        Log.Information("The All accounts link browser is no longer installed and was reset to the system default.");
        _dialogService.InfoBarMessage(Translator.LinkBrowser_StaleTitle,
            string.Format(Translator.LinkBrowser_StaleMessageAllAccounts, staleBrowser.DisplayName),
            InfoBarMessageType.Warning);
    }

    private async Task ResetAccountBrowserAsync(MailAccountPreferences preferences, BrowserOption staleBrowser, string accountName)
    {
        SetCustomBrowser(preferences, null);
        await _accountService.UpdateAccountPreferencesAsync(preferences).ConfigureAwait(false);
        Log.Information("An account's link browser is no longer installed and was reset to the system default.");
        _dialogService.InfoBarMessage(Translator.LinkBrowser_StaleTitle,
            string.Format(Translator.LinkBrowser_StaleMessage, staleBrowser.DisplayName, accountName),
            InfoBarMessageType.Warning);
    }
}
