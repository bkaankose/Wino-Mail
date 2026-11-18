#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>The PIN entry rules the lock page and the verification dialog share.</summary>
internal static class AppLockPinInput
{
    /// <summary>Drops anything but digits. Returns true when the field had to be corrected.</summary>
    public static bool KeepDigitsOnly(PasswordBox pinBox)
    {
        var password = pinBox.Password ?? string.Empty;
        if (password.All(char.IsAsciiDigit))
            return false;

        pinBox.Password = new string(password.Where(char.IsAsciiDigit).ToArray());
        return true;
    }

    public static bool CanSubmit(PasswordBox pinBox)
        => (pinBox.Password?.Length ?? 0) >= AppLockPinRules.MinimumLength;

    // PBKDF2 with 200,000 iterations takes long enough to stall a frame, so it runs off the UI thread.
    public static Task<AppLockPinVerification> VerifyAsync(IAppLockService appLockService, string pin)
        => Task.Run(() => appLockService.VerifyPin(pin));

    public static string GetFailureMessage(AppLockPinVerification verification)
        => verification.Status == AppLockPinVerificationStatus.Throttled
            ? string.Format(Translator.AppLock_PinThrottled, Math.Max(1, (int)Math.Ceiling(verification.RetryAfter.TotalSeconds)))
            : Translator.AppLock_PinWrong;
}
