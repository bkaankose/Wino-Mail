using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.AppLock;

/// <summary>
/// "Confirm it's you" (Windows AppLockVerificationDialog): the PIN before a sensitive step such as changing or turning
/// off the lock. The dialog stays open on a wrong PIN and shows the throttle wait after too many.
/// </summary>
internal static class AppLockVerificationDialog
{
    /// <summary>True when the PIN was right. Main thread.</summary>
    public static async Task<bool> PresentAsync(Gtk.Widget owner, IAppLockService appLock)
    {
        var form = new FormDialog(Translator.AppLock_VerifyTitle, Translator.AppLock_VerifyDescription, Translator.AppLock_VerifyConfirm);
        var pin = WinoSettingsRows.Password(Translator.AppLock_PinFieldName);
        pin.SetActivatesDefault(true);
        form.AddGroup().Add(pin);
        var correcting = false;
        form.CanConfirm = () => pin.GetText().Length >= AppLockPinRules.MinimumLength;
        form.ValidateAsync = () =>
        {
            var value = pin.GetText();
            var completion = new TaskCompletionSource<string?>();
            // PBKDF2 runs off the main thread; the result is handed back on it, where the dialog continues.
            _ = Task.Run(() => appLock.VerifyPin(value)).ContinueWith(task => MainLoop.Post(() =>
            {
                pin.SetText(string.Empty);
                completion.SetResult(task.IsCompletedSuccessfully ? Message(task.Result) : Translator.AppLock_PinWrong);
            }), TaskScheduler.Default);
            return completion.Task;
        };
        form.Own(GtkSignals.OnChanged(pin, () =>
        {
            if (correcting) return;
            var text = pin.GetText();
            if (!text.All(char.IsAsciiDigit))
            {
                correcting = true;
                try { pin.SetText(new string([.. text.Where(char.IsAsciiDigit)])); }
                finally { correcting = false; }
            }
            form.Refresh();
        }));
        form.SetInitialFocus(pin);
        var confirmed = await form.PresentAsync(owner);
        pin.SetText(string.Empty);
        return confirmed;
    }

    private static string? Message(AppLockPinVerification verification) => verification.Status switch
    {
        AppLockPinVerificationStatus.Success => null,
        AppLockPinVerificationStatus.Throttled => string.Format(Translator.AppLock_PinThrottled, Math.Max(1, (int)Math.Ceiling(verification.RetryAfter.TotalSeconds))),
        _ => Translator.AppLock_PinWrong,
    };
}
