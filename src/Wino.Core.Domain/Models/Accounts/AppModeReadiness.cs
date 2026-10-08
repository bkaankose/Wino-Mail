#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// The result of asking whether an app mode is usable right now.
/// <paramref name="AttentionAccount"/> is the account the user should sign in to when the
/// state is <see cref="AppModeReadinessState.SignInRequired"/>.
/// </summary>
public sealed record AppModeReadiness(
    WinoApplicationMode Mode,
    AppModeReadinessState State,
    MailAccount? AttentionAccount = null)
{
    public bool IsReady => State == AppModeReadinessState.Ready;

    public static AppModeReadiness Ready(WinoApplicationMode mode) => new(mode, AppModeReadinessState.Ready);

    /// <summary>
    /// Whether an account has the mode turned on. Calendar counts a local-only calendar
    /// (enabled without provider access) the same way the calendar pane does.
    /// </summary>
    public static bool IsModeEnabled(MailAccount account, WinoApplicationMode mode)
    {
        if (account == null)
            return false;

        return mode switch
        {
            WinoApplicationMode.Mail => account.IsMailAccessGranted,
            WinoApplicationMode.Calendar => account.IsCalendarAccessEnabled || account.IsCalendarAccessGranted,
            WinoApplicationMode.Contacts => account.IsContactAccessEnabled,
            WinoApplicationMode.Tasks => account.IsTaskAccessEnabled,
            _ => true
        };
    }

    /// <summary>Whether the account needs the user to sign in again before the mode can load.</summary>
    public static bool NeedsSignIn(MailAccount account, WinoApplicationMode mode)
    {
        if (account == null)
            return false;

        if (account.AttentionReason is AccountAttentionReason.InvalidCredentials or AccountAttentionReason.CertificateValidationFailed)
            return true;

        return mode switch
        {
            // A local-backed mode never needs a provider sign-in, even with a stale flag.
            WinoApplicationMode.Contacts => account.IsContactReauthorizationRequired &&
                                            account.ContactIntegrationSource != AccountIntegrationSource.Local,
            WinoApplicationMode.Tasks => account.IsTaskReauthorizationRequired &&
                                         account.TaskIntegrationSource != AccountIntegrationSource.Local,
            _ => false
        };
    }

    /// <summary>
    /// Pure evaluation shared by every mode.
    /// </summary>
    /// <param name="mode">The mode being evaluated. Settings is always ready.</param>
    /// <param name="accounts">Every local account.</param>
    /// <param name="hasModeData">
    /// Whether an account already has something the mode works with: folders, calendars,
    /// task lists or an address book.
    /// </param>
    public static AppModeReadiness Evaluate(
        WinoApplicationMode mode,
        IReadOnlyCollection<MailAccount> accounts,
        Func<MailAccount, bool> hasModeData)
    {
        if (mode == WinoApplicationMode.Settings)
            return Ready(mode);

        if (accounts == null || accounts.Count == 0)
            return new AppModeReadiness(mode, AppModeReadinessState.NoAccounts);

        var enabledAccounts = accounts.Where(account => IsModeEnabled(account, mode)).ToList();
        if (enabledAccounts.Count == 0)
            return new AppModeReadiness(mode, AppModeReadinessState.FeatureDisabled);

        if (hasModeData == null || enabledAccounts.Any(hasModeData))
            return Ready(mode);

        // Nothing to work with yet. A sign-in the user can do is more useful than waiting.
        var signInAccount = enabledAccounts.FirstOrDefault(account => NeedsSignIn(account, mode));
        if (signInAccount != null)
            return new AppModeReadiness(mode, AppModeReadinessState.SignInRequired, signInAccount);

        return new AppModeReadiness(mode, AppModeReadinessState.WaitingForSynchronization);
    }
}
