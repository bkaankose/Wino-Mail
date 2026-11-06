#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Browsers;

namespace Wino.Mail.ViewModels;

public partial class PrivacySecurityPageViewModel
{
    private readonly List<MailAccount> _linkBrowserAccounts = [];

    /// <summary>Parallel to <see cref="LinkBrowserOptions"/>; null is System default.</summary>
    private readonly List<BrowserOption?> _linkBrowserChoices = [null];

    private bool _isSyncingLinkBrowser;

    public bool IsLinkBrowserSupported => _linkBrowserService.IsBrowserSelectionSupported;

    [ObservableProperty]
    public partial List<string> LinkBrowserScopeOptions { get; set; } = [Translator.SettingsActionScope_AllAccounts];

    /// <summary>Hidden with a single account unless that account still carries an override to undo.</summary>
    [ObservableProperty]
    public partial bool IsLinkBrowserScopeAvailable { get; set; }

    [ObservableProperty]
    public partial int SelectedLinkBrowserScopeIndex { get; set; }

    [ObservableProperty]
    public partial string LinkBrowserScopeDescription { get; set; } = Translator.SettingsLinkBrowser_AllAccountsDescription;

    [ObservableProperty]
    public partial bool CanUseAllAccountsLinkBrowser { get; set; }

    [ObservableProperty]
    public partial List<string> LinkBrowserOptions { get; set; } = [Translator.SettingsLinkBrowser_SystemDefault];

    [ObservableProperty]
    public partial int SelectedLinkBrowserIndex { get; set; }

    [ObservableProperty]
    public partial string? LinkBrowserDescription { get; set; }

    [ObservableProperty]
    public partial bool HasCustomisedLinkBrowserAccounts { get; set; }

    [ObservableProperty]
    public partial string CustomisedLinkBrowserAccountsText { get; set; } = string.Empty;

    private MailAccount? SelectedLinkBrowserAccount
        => SelectedLinkBrowserScopeIndex > 0 && SelectedLinkBrowserScopeIndex <= _linkBrowserAccounts.Count
            ? _linkBrowserAccounts[SelectedLinkBrowserScopeIndex - 1]
            : null;

    private async Task RefreshLinkBrowserSettingsAsync()
    {
        if (!IsLinkBrowserSupported) return;

        try
        {
            await LoadLinkBrowsersAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load the link browser settings.");
        }
    }

    private async Task LoadLinkBrowsersAsync()
    {
        // Listing first resets selections that are no longer installed before they are shown.
        var catalog = await _linkBrowserService.GetBrowsersAsync();
        var accounts = (await _accountService.GetAccountsAsync()).Where(account => account.Preferences != null).ToList();

        await ExecuteUIThread(() =>
        {
            _linkBrowserAccounts.Clear();
            _linkBrowserAccounts.AddRange(accounts);

            _linkBrowserChoices.Clear();
            _linkBrowserChoices.Add(null);
            _linkBrowserChoices.AddRange(catalog.Browsers);

            // A catalog that could not be read must still show what is selected instead of System default.
            foreach (var selection in accounts
                .Select(account => _linkBrowserService.GetCustomBrowser(account.Preferences))
                .Prepend(_linkBrowserService.AllAccountsBrowser))
            {
                if (selection is not null && FindLinkBrowserChoice(selection) < 0)
                    _linkBrowserChoices.Add(selection);
            }

            _isSyncingLinkBrowser = true;

            try
            {
                LinkBrowserOptions = [.. _linkBrowserChoices.Select(choice => choice?.DisplayName ?? Translator.SettingsLinkBrowser_SystemDefault)];
                LinkBrowserScopeOptions = [Translator.SettingsActionScope_AllAccounts, .. accounts.Select(GetScopeDisplayName)];
                LinkBrowserDescription = catalog.IsComplete ? null : Translator.SettingsLinkBrowser_Unavailable;
                IsLinkBrowserScopeAvailable = accounts.Count > 1 || accounts.Any(account => account.Preferences.HasCustomLinkBrowser);
                SelectedLinkBrowserScopeIndex = 0;
            }
            finally
            {
                _isSyncingLinkBrowser = false;
            }

            SyncLinkBrowserScope();
        });
    }

    private static string GetScopeDisplayName(MailAccount account)
        => string.IsNullOrWhiteSpace(account.Address) || string.Equals(account.Name, account.Address, StringComparison.OrdinalIgnoreCase)
            ? account.Name
            : $"{account.Name} ({account.Address})";

    private int FindLinkBrowserChoice(BrowserOption? browser)
        => browser is null ? 0 : _linkBrowserChoices.FindIndex(choice => choice is not null && choice.IsSameTarget(browser));

    private BrowserOption? GetEffectiveLinkBrowser(MailAccount? account)
        => account?.Preferences is { HasCustomLinkBrowser: true } preferences
            ? _linkBrowserService.GetCustomBrowser(preferences)
            : _linkBrowserService.AllAccountsBrowser;

    partial void OnSelectedLinkBrowserScopeIndexChanged(int value)
    {
        if (_isSyncingLinkBrowser) return;

        SyncLinkBrowserScope();
    }

    partial void OnSelectedLinkBrowserIndexChanged(int value)
    {
        if (_isSyncingLinkBrowser || value < 0 || value >= _linkBrowserChoices.Count) return;

        _ = ObserveLinkBrowserSaveAsync(ApplyLinkBrowserAsync(_linkBrowserChoices[value]));
    }

    private void SyncLinkBrowserScope()
    {
        _isSyncingLinkBrowser = true;

        try
        {
            SelectedLinkBrowserIndex = Math.Max(0, FindLinkBrowserChoice(GetEffectiveLinkBrowser(SelectedLinkBrowserAccount)));
        }
        finally
        {
            _isSyncingLinkBrowser = false;
        }

        RefreshLinkBrowserScopeState();
    }

    private void RefreshLinkBrowserScopeState()
    {
        var account = SelectedLinkBrowserAccount;
        var isCustomised = account?.Preferences?.HasCustomLinkBrowser == true;

        CanUseAllAccountsLinkBrowser = isCustomised;
        LinkBrowserScopeDescription = account is null
            ? Translator.SettingsLinkBrowser_AllAccountsDescription
            : string.Format(isCustomised ? Translator.SettingsLinkBrowser_CustomisedDescription : Translator.SettingsLinkBrowser_FollowingDescription, account.Name);

        var customisedNames = string.Join(", ", _linkBrowserAccounts
            .Where(scopeAccount => scopeAccount.Preferences?.HasCustomLinkBrowser == true)
            .Select(scopeAccount => scopeAccount.Name));

        HasCustomisedLinkBrowserAccounts = customisedNames.Length > 0;
        CustomisedLinkBrowserAccountsText = HasCustomisedLinkBrowserAccounts
            ? string.Format(Translator.SettingsLinkBrowser_CustomisedAccounts, customisedNames)
            : string.Empty;
    }

    private async Task ApplyLinkBrowserAsync(BrowserOption? browser)
    {
        if (SelectedLinkBrowserAccount is { Preferences: { } preferences })
        {
            _linkBrowserService.SetCustomBrowser(preferences, browser);
            await _accountService.UpdateAccountPreferencesAsync(preferences);
        }
        else
        {
            _linkBrowserService.AllAccountsBrowser = browser;
            await DropMatchingLinkBrowserOverridesAsync();
        }

        await ExecuteUIThread(RefreshLinkBrowserScopeState);
    }

    /// <summary>
    /// An override equal to the new All accounts browser is no override. Dropping it keeps the account
    /// following future changes instead of silently freezing it on a copy.
    /// </summary>
    private async Task DropMatchingLinkBrowserOverridesAsync()
    {
        foreach (var account in _linkBrowserAccounts)
        {
            if (account.Preferences is not { HasCustomLinkBrowser: true } preferences) continue;

            _linkBrowserService.SetCustomBrowser(preferences, _linkBrowserService.GetCustomBrowser(preferences));

            if (!preferences.HasCustomLinkBrowser)
                await _accountService.UpdateAccountPreferencesAsync(preferences);
        }
    }

    private static async Task ObserveLinkBrowserSaveAsync(Task save)
    {
        try { await save; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Failed to save the link browser."); }
    }

    [RelayCommand]
    private async Task UseAllAccountsLinkBrowserAsync()
    {
        if (SelectedLinkBrowserAccount is not { Preferences: { } preferences }) return;

        _linkBrowserService.SetCustomBrowser(preferences, _linkBrowserService.AllAccountsBrowser);
        await _accountService.UpdateAccountPreferencesAsync(preferences);
        SyncLinkBrowserScope();
    }
}
