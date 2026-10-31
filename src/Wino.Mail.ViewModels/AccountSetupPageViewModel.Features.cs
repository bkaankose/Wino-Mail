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
/// The mode steps after resolution. They come before sign-in so OAuth asks only for the scopes of the chosen modes;
/// a calendar or contacts service discovery did not find can be typed in on its step.
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

    /// <summary>The manual DAV entry offers a separate sign-in only when there is a mail sign-in to share.</summary>
    public bool IsUseMailSignInForDavVisible => Capabilities.IsMailEnabled;

    private bool IsCalendarRemote => Capabilities.CalendarMode == AccountCapabilityMode.Provider;
    private bool IsContactRemote => Capabilities.ContactMode == AccountCapabilityMode.Provider;

    private Uri? CalDavServiceUri => !IsCalendarRemote
        ? null
        : IsCalDavManualEntryVisible ? ParseDavUri(ManualCalDavUrl) : _resolution?.Support.Calendar.ServiceUri;

    private Uri? CardDavServiceUri => !IsContactRemote
        ? null
        : IsCardDavManualEntryVisible ? ParseDavUri(ManualCardDavUrl) : _resolution?.Support.Contacts.ServiceUri;

    private ImapCalendarSupportMode CalendarSupportMode => Capabilities.CalendarMode switch
    {
        AccountCapabilityMode.Provider => ImapCalendarSupportMode.CalDav,
        AccountCapabilityMode.Local => ImapCalendarSupportMode.LocalOnly,
        _ => ImapCalendarSupportMode.Disabled
    };

    /// <summary>
    /// Leaves the To Do step: to the sign-in when a mode needs a server, otherwise straight to Add account, since a local
    /// account has nothing to sign in to.
    /// </summary>
    private async Task ContinueFromFeaturesAsync()
    {
        if (Phase != AccountSetupPhase.Features || !ValidateFeatureSelection())
            return;

        BuildChecks();

        if (!Capabilities.RequiresRemoteService)
        {
            await AddAccountAsync();
            return;
        }

        if (Route == AccountRoute.Imap)
            BuildImapOptions();

        Phase = AccountSetupPhase.SignIn;
    }

    private bool ValidateFeatureSelection()
    {
        FeaturesError = string.Empty;

        if (!Capabilities.HasAnyCapability)
        {
            FeaturesError = IsLocalOnlyMode
                ? Translator.AccountSetup_LocalCapabilityRequired
                : Translator.ProviderSelection_CapabilityValidationMessage;
            return false;
        }

        if ((IsCalendarRemote && IsCalDavManualEntryVisible && CalDavServiceUri == null) ||
            (IsContactRemote && IsCardDavManualEntryVisible && CardDavServiceUri == null))
        {
            FeaturesError = Translator.AccountSetup_ServerAddressInvalid;
            return false;
        }

        return true;
    }

    private void ConfigureFeatures(AccountRouteResolution resolution)
    {
        var support = resolution.Support;
        var isNative = resolution.Route is AccountRoute.Gmail or AccountRoute.Outlook;
        var providerName = isNative ? NativeProviderName : string.Empty;

        ResetFeatureEntries();

        Capabilities.ConfigureProvider(
            support.Calendar.IsSupported,
            support.Contacts.IsSupported,
            support.Tasks.IsSupported,
            isNative ? providerName : Translator.ProviderSelection_SourceCalDav,
            isNative ? providerName : Translator.ProviderSelection_SourceCardDav,
            isNative ? providerName : string.Empty);

        Capabilities.MailMode = support.Mail.IsSupported ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
        Capabilities.CalendarMode = AccountCapabilityMode.Provider;
        Capabilities.ContactMode = AccountCapabilityMode.Provider;
        Capabilities.TaskMode = support.Tasks.IsSupported ? AccountCapabilityMode.Provider : AccountCapabilityMode.Local;
        Capabilities.CoerceUnavailableModes();

        Capabilities.IsCalendarManualEntryAvailable = resolution.Route == AccountRoute.Imap && !support.Calendar.IsSupported;
        Capabilities.IsContactManualEntryAvailable = resolution.Route == AccountRoute.Imap && !support.Contacts.IsSupported;

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

        return IsNativeRoute ? AccountIntegrationSource.Provider : AccountIntegrationSource.Dav;
    }

    private void OnCapabilitiesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountCapabilitySelection.MailMode))
            OnPropertyChanged(nameof(IsUseMailSignInForDavVisible));
    }

    partial void OnSelectedInitialSynchronizationRangeChanged(InitialSynchronizationRangeOption? value) => NotifyStepContentChanged();

    partial void OnManualCalDavUrlChanged(string value) => ClearFeaturesError();

    partial void OnManualCardDavUrlChanged(string value) => ClearFeaturesError();

    private void ClearFeaturesError()
    {
        if (HasFeaturesError)
            FeaturesError = string.Empty;
    }

    private void NotifyFeaturesLayoutChanged() => OnPropertyChanged(nameof(IsUseMailSignInForDavVisible));
}
