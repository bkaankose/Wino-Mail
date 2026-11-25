#nullable enable
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// One app of an account (Mail, Calendar, People, To Do) as the account settings show it: off, or on with a source.
/// <see cref="AccountCapabilityMode.Provider"/> is the account's own service, or its CalDAV or CardDAV server when
/// <see cref="IsServiceDavServer"/> is set; <see cref="AccountCapabilityMode.Local"/> is this device.
/// </summary>
public sealed record AccountAppState(
    WinoApplicationMode App,
    AccountCapabilityMode Mode,
    bool IsServiceOffered,
    bool IsDeviceOffered,
    bool IsServiceDavServer,
    bool HasDavServer,
    bool NeedsSignIn)
{
    public bool IsOn => Mode != AccountCapabilityMode.Off;

    public bool IsOffered => IsServiceOffered || IsDeviceOffered;

    public bool HasSourceChoice => IsServiceOffered && IsDeviceOffered;

    /// <summary>A server source that has no server address yet cannot be turned on without one.</summary>
    public bool IsServiceReady => IsServiceOffered && (!IsServiceDavServer || HasDavServer);

    /// <summary>
    /// The source a switch turns the app on with: the account's service, a DAV server only when one is
    /// already connected, otherwise this device.
    /// </summary>
    public AccountCapabilityMode DefaultOnMode => IsServiceReady
        ? AccountCapabilityMode.Provider
        : IsDeviceOffered ? AccountCapabilityMode.Local : AccountCapabilityMode.Provider;
}

/// <summary>
/// Maps the four apps of an account onto the existing access and integration-source columns. Pure, so every
/// head and the capability service read the same state.
/// </summary>
public static class AccountApps
{
    public static readonly WinoApplicationMode[] All =
    [
        WinoApplicationMode.Mail,
        WinoApplicationMode.Calendar,
        WinoApplicationMode.Contacts,
        WinoApplicationMode.Tasks
    ];

    public static AccountCapabilityMode GetMode(MailAccount account, MailProviderTraits traits, WinoApplicationMode app)
    {
        if (account == null)
            return AccountCapabilityMode.Off;

        switch (app)
        {
            case WinoApplicationMode.Mail:
                return account.IsMailAccessGranted ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
            case WinoApplicationMode.Calendar:
                if (!AppModeReadiness.IsModeEnabled(account, WinoApplicationMode.Calendar))
                    return AccountCapabilityMode.Off;

                var calendarSource = account.GetEffectiveCalendarIntegrationSource();
                var isCalendarFromServer = traits.ServesModesFromProvider
                    ? calendarSource == AccountIntegrationSource.Provider && account.IsCalendarAccessGranted
                    : calendarSource == AccountIntegrationSource.Dav;

                return isCalendarFromServer ? AccountCapabilityMode.Provider : AccountCapabilityMode.Local;
            case WinoApplicationMode.Contacts:
                if (!account.IsContactAccessEnabled)
                    return AccountCapabilityMode.Off;

                return account.ContactIntegrationSource == AccountIntegrationSource.Local
                    ? AccountCapabilityMode.Local
                    : AccountCapabilityMode.Provider;
            case WinoApplicationMode.Tasks:
                if (!account.IsTaskAccessEnabled)
                    return AccountCapabilityMode.Off;

                return account.TaskIntegrationSource == AccountIntegrationSource.Local
                    ? AccountCapabilityMode.Local
                    : AccountCapabilityMode.Provider;
            default:
                return AccountCapabilityMode.Off;
        }
    }

    public static AccountAppState Evaluate(MailAccount account, MailProviderTraits traits, WinoApplicationMode app)
    {
        var mode = GetMode(account, traits, app);
        var isLocalOnly = account?.IsLocalOnly == true;
        var isDavServer = !traits.ServesModesFromProvider && traits.SupportsDavIntegration;

        var isServiceOffered = app == WinoApplicationMode.Mail
            ? !isLocalOnly
            : !isLocalOnly && (traits.ServesModesFromProvider || traits.SupportsDavIntegration);

        var isDeviceOffered = app != WinoApplicationMode.Mail;

        var davUrl = app == WinoApplicationMode.Contacts
            ? account?.ServerInformation?.CardDavServiceUrl
            : account?.ServerInformation?.CalDavServiceUrl;

        var needsSignIn = mode == AccountCapabilityMode.Provider && account != null && AppModeReadiness.NeedsSignIn(account, app);

        return new AccountAppState(
            app,
            mode,
            isServiceOffered,
            isDeviceOffered,
            isServiceOffered && isDavServer && app != WinoApplicationMode.Mail,
            !string.IsNullOrWhiteSpace(davUrl),
            needsSignIn);
    }

    public static List<AccountAppState> EvaluateAll(MailAccount account, MailProviderTraits traits)
        => All.Select(app => Evaluate(account, traits, app)).ToList();

    public static int CountOn(MailAccount account, MailProviderTraits traits)
        => All.Count(app => GetMode(account, traits, app) != AccountCapabilityMode.Off);

    /// <summary>An account keeps at least one app; the last one is turned off by removing the account.</summary>
    public static bool IsLastAppOn(MailAccount account, MailProviderTraits traits, WinoApplicationMode app)
        => GetMode(account, traits, app) != AccountCapabilityMode.Off && CountOn(account, traits) <= 1;

    /// <summary>What a change of an app's mode does to the data on this device, so every head asks the same question.</summary>
    public static AccountAppTransition GetTransition(AccountCapabilityMode from, AccountCapabilityMode to)
        => (from, to) switch
        {
            _ when from == to => AccountAppTransition.None,
            (AccountCapabilityMode.Off, _) => AccountAppTransition.TurnOn,
            (_, AccountCapabilityMode.Off) => AccountAppTransition.TurnOff,
            (AccountCapabilityMode.Provider, AccountCapabilityMode.Local) => AccountAppTransition.LeaveServer,
            _ => AccountAppTransition.JoinServer
        };
}

public enum AccountAppTransition
{
    None,
    TurnOn,
    TurnOff,

    /// <summary>The server copies leave this device; the app continues empty on this device.</summary>
    LeaveServer,

    /// <summary>The app starts syncing with a server; items kept only on this device stay beside the synced ones.</summary>
    JoinServer
}
