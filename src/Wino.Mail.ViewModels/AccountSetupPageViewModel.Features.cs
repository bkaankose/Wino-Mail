#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// What the account is used for. <see cref="Capabilities"/> holds one mode per app; the review step's rows edit it.
/// On the IMAP route Calendar and To Do share one CalDAV server: the one discovery found, or one the user enters.
/// </summary>
public partial class AccountSetupPageViewModel
{
    public AccountCapabilitySelection Capabilities { get; } = new();

    public List<InitialSynchronizationRangeOption> InitialSynchronizationRanges { get; } =
    [
        new(InitialSynchronizationRange.ThreeMonths, Translator.AccountCreation_InitialSynchronization_3Months),
        new(InitialSynchronizationRange.SixMonths, Translator.AccountCreation_InitialSynchronization_6Months),
        new(InitialSynchronizationRange.NineMonths, Translator.AccountCreation_InitialSynchronization_9Months),
        new(InitialSynchronizationRange.OneYear, Translator.AccountCreation_InitialSynchronization_Year),
        new(InitialSynchronizationRange.Everything, Translator.AccountCreation_InitialSynchronization_Everything)
    ];

    [ObservableProperty]
    public partial InitialSynchronizationRangeOption? SelectedInitialSynchronizationRange { get; set; }

    [ObservableProperty]
    public partial bool IsCalDavManualEntryVisible { get; set; }

    [ObservableProperty]
    public partial string ManualCalDavUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCardDavManualEntryVisible { get; set; }

    [ObservableProperty]
    public partial string ManualCardDavUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseMailSignInForDav { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeaturesError))]
    public partial string FeaturesError { get; set; } = string.Empty;

    public bool HasFeaturesError => !string.IsNullOrWhiteSpace(FeaturesError);

    private bool IsCalendarRemote => Capabilities.CalendarMode == AccountCapabilityMode.Provider;
    private bool IsContactRemote => Capabilities.ContactMode == AccountCapabilityMode.Provider;

    /// <summary>Discovery found POP3 servers: mail only, kept on this device, with nothing else to sync.</summary>
    private bool IsPop3Route => IsImapRoute && _resolution?.Imap?.IsPop3 == true;

    /// <summary>An IMAP account can sync calendar, contacts and tasks through DAV; a POP3 account cannot.</summary>
    private bool IsDavRoute => IsImapRoute && !IsPop3Route;

    /// <summary>To Do synced through the IMAP account's CalDAV server; the other routes sync tasks through their own API.</summary>
    private bool IsTaskDavRemote => IsDavRoute && Capabilities.TaskMode == AccountCapabilityMode.Provider;

    private bool UsesCalDav => IsCalendarRemote || IsTaskDavRemote;

    private Uri? CalDavServiceUri => !UsesCalDav
        ? null
        : IsCalDavManualEntryVisible ? ParseDavUri(ManualCalDavUrl) : _foundCalDavUri ?? _resolution?.Support.Calendar.ServiceUri;

    private Uri? CardDavServiceUri => !IsContactRemote
        ? null
        : IsCardDavManualEntryVisible ? ParseDavUri(ManualCardDavUrl) : _foundCardDavUri ?? _resolution?.Support.Contacts.ServiceUri;

    private ImapCalendarSupportMode CalendarSupportMode => Capabilities.CalendarMode switch
    {
        AccountCapabilityMode.Provider => ImapCalendarSupportMode.CalDav,
        AccountCapabilityMode.Local => ImapCalendarSupportMode.LocalOnly,
        _ => ImapCalendarSupportMode.Disabled
    };

    /// <summary>
    /// Leaves the review step. A local account and the signed-in IMAP account are added from here; an Exchange account
    /// that signs in with a password goes on to its sign-in step.
    /// </summary>
    private async Task ContinueFromReviewAsync()
    {
        if (Phase != AccountSetupPhase.Features || IsReviewBusy || !await ValidateReviewAsync())
            return;

        if (IsLocalOnlyMode || !Capabilities.RequiresRemoteService)
        {
            BuildChecks();
            await AddAccountAsync();
            return;
        }

        if (IsImapRoute)
        {
            if (await PrepareImapServerAsync())
                await AddAccountAsync();

            return;
        }

        BuildChecks();
        Phase = AccountSetupPhase.SignIn;
    }

    /// <summary>The account name, at least one app, and a usable address for every server the user entered.</summary>
    private async Task<bool> ValidateReviewAsync()
    {
        AccountNameError = string.Empty;

        var name = AccountName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            AccountNameError = Translator.AccountSetup_AccountNameRequired;
            return false;
        }

        if (await IsAccountNameTakenAsync(name))
            return false;

        return ValidateFeatureSelection();
    }

    private bool ValidateFeatureSelection()
    {
        FeaturesError = string.Empty;
        ClearRowErrors();

        if (!Capabilities.HasAnyCapability)
        {
            FeaturesError = IsLocalOnlyMode
                ? Translator.AccountSetup_LocalCapabilityRequired
                : Translator.ProviderSelection_CapabilityValidationMessage;
            return false;
        }

        var isValid = true;

        if (UsesCalDav && IsCalDavManualEntryVisible && CalDavServiceUri == null)
        {
            SetRowError(AccountSetupAppKind.Calendar, Translator.AccountSetup_ServerAddressInvalid);
            SetRowError(AccountSetupAppKind.Tasks, Translator.AccountSetup_ServerAddressInvalid);
            isValid = false;
        }

        if (IsContactRemote && IsCardDavManualEntryVisible && CardDavServiceUri == null)
        {
            SetRowError(AccountSetupAppKind.Contacts, Translator.AccountSetup_ServerAddressInvalid);
            isValid = false;
        }

        return isValid;
    }

    private void ConfigureFeatures(AccountRouteResolution resolution)
    {
        var support = resolution.Support;
        var isNative = resolution.Route is AccountRoute.Gmail or AccountRoute.Outlook or AccountRoute.Exchange;
        var providerName = resolution.Route == AccountRoute.Exchange
            ? ExchangeProviderName
            : isNative ? NativeProviderName : string.Empty;

        ResetFeatureEntries();

        // An IMAP account syncs To Do through the same CalDAV service as Calendar.
        var isTaskSupported = isNative ? support.Tasks.IsSupported : support.Calendar.IsSupported;

        Capabilities.ConfigureProvider(
            support.Calendar.IsSupported,
            support.Contacts.IsSupported,
            isTaskSupported,
            isNative ? providerName : Translator.ProviderSelection_SourceCalDav,
            isNative ? providerName : Translator.ProviderSelection_SourceCardDav,
            isNative ? providerName : Translator.ProviderSelection_SourceCalDav);

        Capabilities.MailMode = support.Mail.IsSupported ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
        Capabilities.CalendarMode = AccountCapabilityMode.Provider;
        Capabilities.ContactMode = AccountCapabilityMode.Provider;
        Capabilities.TaskMode = isTaskSupported ? AccountCapabilityMode.Provider : AccountCapabilityMode.Local;
        Capabilities.CoerceUnavailableModes();

        // A server that offers calendar, contacts or tasks does not mean the user wants them; on IMAP and POP3 only
        // mail starts on.
        if (resolution.Route == AccountRoute.Imap)
            Capabilities.TurnOffOptionalApps();

        var isDavRoute =resolution.Route == AccountRoute.Imap && resolution.Imap?.IsPop3 != true;
        Capabilities.IsCalendarManualEntryAvailable = isDavRoute;
        Capabilities.IsContactManualEntryAvailable = isDavRoute;

        NotifyFeaturesLayoutChanged();
    }

    /// <summary>A local account offers calendar, contacts and To Do on this device only; each can still be turned off.</summary>
    private void ConfigureLocalFeatures()
    {
        ResetFeatureEntries();

        Capabilities.ConfigureProvider(false, false, false, string.Empty, string.Empty, string.Empty);
        Capabilities.MailMode = AccountCapabilityMode.Off;
        Capabilities.CalendarMode = AccountCapabilityMode.Local;
        Capabilities.ContactMode = AccountCapabilityMode.Local;
        Capabilities.TaskMode = AccountCapabilityMode.Local;

        NotifyFeaturesLayoutChanged();
    }

    private void ResetFeatureEntries()
    {
        IsCalDavManualEntryVisible = false;
        IsCardDavManualEntryVisible = false;
        ManualCalDavUrl = string.Empty;
        ManualCardDavUrl = string.Empty;
        UseMailSignInForDav = true;
        FeaturesError = string.Empty;
        Capabilities.IsCalendarManualEntryAvailable = false;
        Capabilities.IsContactManualEntryAvailable = false;
        ResetDavState();
    }

    private static Uri? ParseDavUri(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    private AccountIntegrationSource ToIntegrationSource(AccountCapabilityMode mode)
    {
        if (mode != AccountCapabilityMode.Provider)
            return AccountIntegrationSource.Local;

        return IsNativeRoute || IsExchangeRoute ? AccountIntegrationSource.Provider : AccountIntegrationSource.Dav;
    }

    private void OnCapabilitiesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountCapabilitySelection.HasAnyCapability) or nameof(AccountCapabilitySelection.RequiresRemoteService) or nameof(AccountCapabilitySelection.IsMailEnabled))
            NotifyStepContentChanged();
    }

    partial void OnSelectedInitialSynchronizationRangeChanged(InitialSynchronizationRangeOption? value) => NotifyStepContentChanged();

    partial void OnUseMailSignInForDavChanged(bool value) => NotifyReviewStateChanged();

    private void NotifyFeaturesLayoutChanged() => NotifyReviewStateChanged();
}
