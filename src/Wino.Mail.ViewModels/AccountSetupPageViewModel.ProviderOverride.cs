#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Validation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// "Change provider": the user overrides the detected route. Google, Microsoft and a catalog provider resolve without a
/// network call; Exchange Server and Other (IMAP) rerun only that discovery stage. The steps then restart at Mail with
/// the new route's defaults, and the strip says "Chosen by you".
/// </summary>
public partial class AccountSetupPageViewModel
{
    /// <summary>The choice the current resolution was forced with; Try again reruns it. Null for detection.</summary>
    private AccountSetupProviderChoice? _forcedChoice;

    public List<AccountSetupProviderChoice> ProviderChoices { get; } =
    [
        new(AccountSetupProviderChoiceKind.Google,
            Translator.AccountSetup_ProviderChoice_Google,
            Translator.AccountSetup_ProviderChoice_GoogleDescription,
            WinoIconGlyph.Google,
            AccountRoute.Gmail),
        new(AccountSetupProviderChoiceKind.Microsoft,
            Translator.AccountSetup_ProviderChoice_Microsoft,
            Translator.AccountSetup_ProviderChoice_MicrosoftDescription,
            WinoIconGlyph.Microsoft,
            AccountRoute.Outlook),
        new(AccountSetupProviderChoiceKind.Exchange,
            Translator.AccountSetup_ProviderChoice_Exchange,
            Translator.AccountSetup_ProviderChoice_ExchangeDescription,
            WinoIconGlyph.Exchange,
            AccountRoute.Exchange),
        new(AccountSetupProviderChoiceKind.ICloud,
            Translator.AccountSetup_ProviderChoice_iCloud,
            Translator.AccountSetup_ProviderChoice_iCloudDescription,
            WinoIconGlyph.Apple,
            AccountRoute.Imap,
            SpecialImapProvider.iCloud),
        new(AccountSetupProviderChoiceKind.Yahoo,
            Translator.AccountSetup_ProviderChoice_Yahoo,
            string.Empty,
            WinoIconGlyph.Yahoo,
            AccountRoute.Imap,
            SpecialImapProvider.Yahoo),
        new(AccountSetupProviderChoiceKind.Imap,
            Translator.AccountSetup_ProviderChoice_Imap,
            Translator.AccountSetup_ProviderChoice_ImapDescription,
            WinoIconGlyph.IMAP,
            AccountRoute.Imap)
    ];

    /// <summary>The picker entry of the current route; null before a route is found or for another catalog provider.</summary>
    public AccountSetupProviderChoice? CurrentProviderChoice
        => CurrentProviderChoiceKind is { } kind ? ProviderChoices.FirstOrDefault(choice => choice.Kind == kind) : null;

    public bool IsRouteManual => _resolution?.IsManual == true;

    /// <summary>MX, the user realm and an autodiscovered IMAP host speak for the domain, not for this mailbox.</summary>
    public bool IsRouteEvidenceDomainLevel => !IsRouteManual &&
                                              _resolution?.Evidence is AccountRouteEvidence.Mx or AccountRouteEvidence.UserRealm or AccountRouteEvidence.ImapHost;

    /// <summary>The strip's tertiary line: why Wino chose the provider when the evidence is domain-level, or "Chosen by you".</summary>
    public string RouteReasonText
    {
        get
        {
            var resolution = _resolution;
            if (resolution == null || !IsRouteFound)
                return string.Empty;

            if (resolution.IsManual)
                return Translator.AccountSetup_Why_Manual;

            return resolution.Evidence switch
            {
                AccountRouteEvidence.Mx => string.Format(
                    resolution.Route == AccountRoute.Gmail ? Translator.AccountSetup_Why_GoogleMx : Translator.AccountSetup_Why_MicrosoftMx,
                    resolution.Domain),
                AccountRouteEvidence.UserRealm => string.Format(Translator.AccountSetup_Why_UserRealm, resolution.Domain),
                AccountRouteEvidence.ImapHost => string.Format(resolution.Route switch
                {
                    AccountRoute.Gmail => Translator.AccountSetup_Why_GoogleImapHost,
                    AccountRoute.Outlook => Translator.AccountSetup_Why_MicrosoftImapHost,
                    _ => Translator.AccountSetup_Why_ImapHost
                }, ImapHostName),
                _ => string.Empty
            };
        }
    }

    /// <summary>The picker's caption: why Wino chose the current provider, or that it found none.</summary>
    public string ProviderPickerCaption
    {
        get
        {
            if (_resolution == null || !IsRouteFound)
                return string.Format(Translator.AccountSetup_ProviderPicker_NotFoundCaption, AddressDomain);

            return IsRouteEvidenceDomainLevel
                ? string.Format(Translator.AccountSetup_ProviderPicker_DomainLevelCaption, RouteReasonText)
                : string.Format(Translator.AccountSetup_ProviderPicker_CertainCaption, RouteShortTitle);
        }
    }

    public bool IsChangeProviderVisible => IsChangeAddressVisible && IsRouteFound;

    /// <summary>The first discovery found nothing; a rerun after a choice offers the manual path instead.</summary>
    public bool IsChooseProviderVisible => Phase == AccountSetupPhase.NotFound && _forcedChoice == null && !IsLocalOnlyMode;

    public string ChangeProviderAutomationName => HasRouteShortTitle
        ? $"{Translator.AccountSetup_ChangeProvider}, {RouteShortTitle}"
        : Translator.AccountSetup_ChangeProvider;

    public string NotFoundTitle => IsExchangeRediscovery ? Translator.AccountSetup_NotFound_ExchangeTitle : Translator.AccountSetup_NotFoundTitle;

    public string NotFoundManualText => IsExchangeRediscovery ? Translator.AccountSetup_EnterServerAddress : Translator.AccountSetup_SetUpManually;

    private bool IsExchangeRediscovery => _forcedChoice?.Kind == AccountSetupProviderChoiceKind.Exchange;

    private AccountSetupProviderChoiceKind? CurrentProviderChoiceKind => IsRouteFound
        ? Route switch
        {
            AccountRoute.Gmail => AccountSetupProviderChoiceKind.Google,
            AccountRoute.Outlook => AccountSetupProviderChoiceKind.Microsoft,
            AccountRoute.Exchange => AccountSetupProviderChoiceKind.Exchange,
            _ => RouteSpecialImapProvider switch
            {
                SpecialImapProvider.iCloud => AccountSetupProviderChoiceKind.ICloud,
                SpecialImapProvider.Yahoo => AccountSetupProviderChoiceKind.Yahoo,
                SpecialImapProvider.None => AccountSetupProviderChoiceKind.Imap,
                _ => null
            }
        }
        : null;

    /// <summary>
    /// Replaces the route with the user's choice. The sign-in, the mode selections and a derived account name are
    /// discarded with the old route; the download range stays.
    /// </summary>
    [RelayCommand]
    private async Task ChangeProviderAsync(AccountSetupProviderChoice? choice)
    {
        if (choice == null || IsLocalOnlyMode)
            return;

        var address = EmailAddress?.Trim() ?? string.Empty;
        if (!MailAccountAddressValidator.IsValid(address))
            return;

        if (Phase != AccountSetupPhase.NotFound && CurrentProviderChoiceKind == choice.Kind)
            return;

        CancelOperation();
        await DiscardSignInAsync();

        if (_isAccountNameDerived)
        {
            AccountName = string.Empty;
            _isAccountNameDerived = false;
        }

        await ResolveAsync(address, choice);
    }

    public List<AccountSetupProviderShelfItem> ProviderShelfItems { get; private set; } = [];

    public string ProviderShelfCaption { get; private set; } = string.Empty;

    public bool IsProviderShelfVisible => IsSettingsHost && !IsLocalOnlyMode;

    /// <summary>
    /// Lights the shelf from the typed domain alone: consumer and catalog domains are certain without a network call.
    /// Custom domains stay neutral until discovery runs on Continue, so typing never sends a domain anywhere.
    /// </summary>
    private void RefreshProviderShelf()
    {
        var address = EmailAddress?.Trim() ?? string.Empty;
        var hasDomain = MailAccountAddressValidator.TryGetDomain(address, out var domain) && domain.Contains('.');

        AccountSetupProviderChoiceKind? matched = null;
        var matchedName = string.Empty;

        if (hasDomain)
        {
            switch (KnownMailHosts.GetConsumerProvider(domain))
            {
                case MailProviderType.Gmail:
                    matched = AccountSetupProviderChoiceKind.Google;
                    break;
                case MailProviderType.Outlook:
                    matched = AccountSetupProviderChoiceKind.Microsoft;
                    break;
                default:
                    var catalogProvider = _providerCatalog.Match(address, string.Empty);
                    if (catalogProvider != null)
                    {
                        matched = catalogProvider.SpecialImapProvider switch
                        {
                            SpecialImapProvider.iCloud => AccountSetupProviderChoiceKind.ICloud,
                            SpecialImapProvider.Yahoo => AccountSetupProviderChoiceKind.Yahoo,
                            _ => AccountSetupProviderChoiceKind.Imap
                        };
                        matchedName = catalogProvider.DisplayName;
                    }
                    break;
            }
        }

        ProviderShelfItems = ProviderChoices
            .Select(choice => new AccountSetupProviderShelfItem(
                choice.Title,
                choice.Glyph,
                choice.Kind == AccountSetupProviderChoiceKind.Imap ? Translator.AccountSetup_Shelf_ImapLabel : string.Empty,
                IsMatched: matched == choice.Kind,
                IsDimmed: matched != null && matched != choice.Kind))
            .ToList();

        if (matched is { } kind)
        {
            if (string.IsNullOrWhiteSpace(matchedName))
                matchedName = ProviderChoices.First(choice => choice.Kind == kind).Title;
            ProviderShelfCaption = string.Format(Translator.AccountSetup_Shelf_Recognized, matchedName);
        }
        else
        {
            ProviderShelfCaption = hasDomain
                ? string.Format(Translator.AccountSetup_Shelf_CheckDomain, domain)
                : Translator.AccountSetup_Shelf_Default;
        }

        OnPropertyChanged(nameof(ProviderShelfItems));
        OnPropertyChanged(nameof(ProviderShelfCaption));
        OnPropertyChanged(nameof(IsProviderShelfVisible));
    }

    private void NotifyProviderOverrideChanged()
    {
        OnPropertyChanged(nameof(CurrentProviderChoice));
        OnPropertyChanged(nameof(IsRouteManual));
        OnPropertyChanged(nameof(IsRouteEvidenceDomainLevel));
        OnPropertyChanged(nameof(RouteReasonText));
        OnPropertyChanged(nameof(ProviderPickerCaption));
        OnPropertyChanged(nameof(IsChangeProviderVisible));
        OnPropertyChanged(nameof(IsChooseProviderVisible));
        OnPropertyChanged(nameof(ChangeProviderAutomationName));
        OnPropertyChanged(nameof(NotFoundTitle));
        OnPropertyChanged(nameof(NotFoundManualText));
    }
}
