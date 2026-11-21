#nullable enable
using System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Mail.ViewModels.AccountSettings;

/// <summary>The words every head uses for an account's apps and their sources.</summary>
public static class AccountAppPresenter
{
    private const string Separator = " · ";

    public static string GetTitle(WinoApplicationMode app) => app switch
    {
        WinoApplicationMode.Calendar => Translator.AccountCapability_Calendar,
        WinoApplicationMode.Contacts => Translator.AccountCapability_People,
        WinoApplicationMode.Tasks => Translator.AccountCapability_ToDo,
        _ => Translator.AccountCapability_Mail
    };

    public static WinoIconGlyph GetIcon(WinoApplicationMode app) => app switch
    {
        WinoApplicationMode.Calendar => WinoIconGlyph.Calendar,
        WinoApplicationMode.Contacts => WinoIconGlyph.People,
        WinoApplicationMode.Tasks => WinoIconGlyph.CheckmarkCircle,
        _ => WinoIconGlyph.Mail
    };

    public static WinoPage GetPage(WinoApplicationMode app) => app switch
    {
        WinoApplicationMode.Calendar => WinoPage.AccountCalendarSettingsPage,
        WinoApplicationMode.Contacts => WinoPage.AccountPeopleSettingsPage,
        WinoApplicationMode.Tasks => WinoPage.AccountToDoSettingsPage,
        _ => WinoPage.AccountMailSettingsPage
    };

    public static string GetUseDescription(WinoApplicationMode app) => app switch
    {
        WinoApplicationMode.Calendar => Translator.AccountApps_UseCalendarDescription,
        WinoApplicationMode.Contacts => Translator.AccountApps_UsePeopleDescription,
        WinoApplicationMode.Tasks => Translator.AccountApps_UseToDoDescription,
        _ => Translator.AccountApps_UseMailDescription
    };

    public static string GetItemsNoun(WinoApplicationMode app) => app switch
    {
        WinoApplicationMode.Calendar => Translator.AccountApps_ItemsCalendar,
        WinoApplicationMode.Contacts => Translator.AccountApps_ItemsPeople,
        WinoApplicationMode.Tasks => Translator.AccountApps_ItemsToDo,
        _ => Translator.AccountApps_ItemsMail
    };

    /// <summary>The account's own service by name, or the kind of DAV server it uses for the app.</summary>
    public static string GetServiceName(MailAccount account, AccountAppState state)
    {
        if (state.IsServiceDavServer)
            return state.App == WinoApplicationMode.Contacts ? Translator.AccountApps_SourceCardDav : Translator.AccountApps_SourceCalDav;

        if (state.App == WinoApplicationMode.Mail && account.ProviderType == MailProviderType.POP3)
            return Translator.ProviderDetail_POP3_Title;

        return new ProviderDetail(account.ProviderType, SpecialImapProvider.None).Name;
    }

    /// <summary>Where the server copy lives, for sentences such as "They stay on dav.example.com".</summary>
    public static string GetServerName(MailAccount account, AccountAppState state)
    {
        if (state.IsServiceDavServer)
        {
            var url = state.App == WinoApplicationMode.Contacts
                ? account.ServerInformation?.CardDavServiceUrl
                : account.ServerInformation?.CalDavServiceUrl;

            var host = GetHost(url);
            if (!string.IsNullOrEmpty(host))
                return host;
        }

        if (state.App == WinoApplicationMode.Mail && account.ServerInformation != null)
        {
            var host = GetHost(account.ServerInformation.IncomingServer);
            if (!string.IsNullOrEmpty(host))
                return host;
        }

        return GetServiceName(account, state);
    }

    public static string GetSourceTitle(MailAccount account, AccountAppState state, AccountCapabilityMode mode)
        => mode == AccountCapabilityMode.Local ? Translator.AccountApps_SourceDevice : GetServiceName(account, state);

    public static string GetSourceDescription(MailAccount account, AccountAppState state, AccountCapabilityMode mode)
        => mode == AccountCapabilityMode.Local
            ? Translator.AccountApps_SourceDeviceDescription
            : string.Format(Translator.AccountApps_SourceServiceDescription, GetServerName(account, state));

    /// <summary>The second line of an app row on the account page: where its data lives, or why it is off.</summary>
    public static string GetSummary(MailAccount account, AccountAppState state)
    {
        if (!state.IsOn)
        {
            return state.HasSourceChoice
                ? string.Format(Translator.AccountApps_SummaryOffWithChoice, GetServiceName(account, state))
                : Translator.AccountApps_SummaryOff;
        }

        if (state.NeedsSignIn)
            return Translator.AccountApps_NeedsSignIn;

        if (state.Mode == AccountCapabilityMode.Local)
            return Translator.AccountApps_SourceDevice;

        if (state.App == WinoApplicationMode.Mail && account.ProviderType == MailProviderType.POP3)
            return Translator.AccountApps_SummaryPop3Mail;

        var serviceName = state.IsServiceDavServer
            ? (state.App == WinoApplicationMode.Contacts ? Translator.ProviderSelection_SourceCardDav : Translator.ProviderSelection_SourceCalDav)
            : GetServiceName(account, state);
        var serverName = GetServerName(account, state);

        return string.Equals(serviceName, serverName, StringComparison.OrdinalIgnoreCase)
            ? serviceName
            : serviceName + Separator + serverName;
    }

    public static string GetHost(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return string.Empty;

        var trimmed = address.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : trimmed;
    }
}
