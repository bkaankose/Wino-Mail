using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.AppLock;

/// <summary>
/// The main window's content while Wino Mail is locked (Windows AppLockScreenPage): the lock title, a PIN field and
/// Unlock. A wrong PIN clears the field; after too many the field and the button wait for the throttle with a countdown.
/// No mail, account or folder widget exists while it shows.
/// </summary>
internal sealed class AppLockScreen : IDisposable
{
    private readonly IAppLockService _appLock;
    private readonly IDispatcher _dispatcher;
    private readonly Adw.ToolbarView _root = Adw.ToolbarView.New();
    private readonly Gtk.PasswordEntry _pin = Gtk.PasswordEntry.New();
    private readonly Gtk.Label _error = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _unlock = Gtk.Button.NewWithLabel(Translator.AppLock_Unlock);
    private readonly List<IDisposable> _subscriptions = [];
    private DateTimeOffset _retryNotBefore;
    private uint _countdown;
    private bool _verifying;
    private bool _correcting;
    private bool _disposed;

    public AppLockScreen(IAppLockService appLock, IDispatcher dispatcher)
    {
        WinoLiveObjects.Track(this);
        _appLock = appLock;
        _dispatcher = dispatcher;

        var page = Adw.StatusPage.New();
        page.SetTitle(GLib.Functions.MarkupEscapeText(Translator.AppLock_LockedTitle, -1));
        page.SetDescription(GLib.Functions.MarkupEscapeText(Translator.AppLock_LockedDescription, -1));
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        content.SetHalign(Gtk.Align.Center);
        var glyph = WinoIcons.Label(WinoIconGlyph.LockClosed, 48);
        glyph.AddCssClass("dim-label");
        glyph.SetMarginBottom(4);
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        column.SetSizeRequest(280, -1);
        column.SetHalign(Gtk.Align.Center);
        _pin.SetShowPeekIcon(true);
        _pin.PlaceholderText = Translator.AppLock_PinFieldName;
        WinoA11y.Label(_pin, Translator.AppLock_PinFieldName);
        column.Append(_pin);
        _error.AddCssClass("error");
        _error.SetWrap(true);
        _error.SetJustify(Gtk.Justification.Center);
        _error.SetMaxWidthChars(36);
        _error.SetVisible(false);
        column.Append(_error);
        _unlock.AddCssClass("suggested-action");
        _unlock.AddCssClass("pill");
        _unlock.SetHalign(Gtk.Align.Center);
        WinoA11y.Rename(_unlock, Translator.AppLock_Unlock);
        column.Append(_unlock);
        page.SetChild(column);

        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        stack.SetValign(Gtk.Align.Center);
        stack.SetVexpand(true);
        glyph.SetHalign(Gtk.Align.Center);
        stack.Append(glyph);
        stack.Append(page);
        _root.AddTopBar(Adw.HeaderBar.New());
        _root.SetContent(stack);

        _subscriptions.Add(GtkSignals.OnClicked(_unlock, () => _ = VerifyAsync()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.PasswordEntry>(h => _pin.OnActivate += h, h => _pin.OnActivate -= h, (_, _) => _ = VerifyAsync()));
        _subscriptions.Add(GtkSignals.OnChanged(_pin, PinChanged));
        Refresh();
    }

    public Gtk.Widget Root => _root;

    public event Action? Unlocked;

    internal string ErrorText => _error.GetVisible() ? _error.GetText() : string.Empty;

    internal bool CanUnlock => _unlock.GetSensitive();

    internal bool IsThrottled => DateTimeOffset.UtcNow < _retryNotBefore;

    public void FocusEntry() => _pin.GrabFocus();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCountdown();
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _pin.SetText(string.Empty);
        Unlocked = null;
    }

#if DEBUG
    internal Task DebugUnlockAsync(string pin)
    {
        _pin.SetText(pin);
        return VerifyAsync();
    }
#endif

    /// <summary>Windows AppLockPinInput.KeepDigitsOnly: the PIN is digits only.</summary>
    private void PinChanged()
    {
        if (_correcting || _disposed) return;
        var text = _pin.GetText();
        if (!text.All(char.IsAsciiDigit))
        {
            _correcting = true;
            try { _pin.SetText(new string([.. text.Where(char.IsAsciiDigit)])); }
            finally { _correcting = false; }
        }
        Refresh();
    }

    private void Refresh()
    {
        var throttled = IsThrottled;
        _unlock.SetSensitive(!_verifying && !throttled && _pin.GetText().Length >= AppLockPinRules.MinimumLength);
        _pin.SetSensitive(!_verifying && !throttled);
    }

    private async Task VerifyAsync()
    {
        if (_disposed || _verifying || IsThrottled || _pin.GetText().Length < AppLockPinRules.MinimumLength) return;
        _verifying = true;
        Refresh();
        var pin = _pin.GetText();
        AppLockPinVerification verification;
        try
        {
            // PBKDF2 with 200,000 iterations would stall frames, so the check runs off the main thread.
            verification = await Task.Run(() => _appLock.VerifyPin(pin));
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "The app lock PIN could not be checked.");
            verification = new AppLockPinVerification(AppLockPinVerificationStatus.Wrong, TimeSpan.Zero);
        }
        await _dispatcher.ExecuteOnUIThread(() => Apply(verification));
    }

    private void Apply(AppLockPinVerification verification)
    {
        if (_disposed) return;
        _verifying = false;
        _pin.SetText(string.Empty);
        if (verification.IsSuccess)
        {
            _error.SetVisible(false);
            _appLock.UnlockSession();
            Unlocked?.Invoke();
            return;
        }
        if (verification.Status == AppLockPinVerificationStatus.Throttled)
        {
            _retryNotBefore = DateTimeOffset.UtcNow + verification.RetryAfter;
            StartCountdown();
        }
        ShowFailure();
        Refresh();
        _pin.GrabFocus();
    }

    private void ShowFailure()
    {
        var remaining = _retryNotBefore - DateTimeOffset.UtcNow;
        _error.SetText(remaining > TimeSpan.Zero
            ? string.Format(Translator.AppLock_PinThrottled, Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)))
            : Translator.AppLock_PinWrong);
        _error.SetVisible(true);
    }

    private void StartCountdown()
    {
        if (_countdown != 0) return;
        _countdown = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 1000, () =>
        {
            if (_disposed || !IsThrottled)
            {
                _countdown = 0;
                if (!_disposed)
                {
                    _error.SetVisible(false);
                    Refresh();
                }
                return false;
            }
            ShowFailure();
            return true;
        });
    }

    private void StopCountdown()
    {
        if (_countdown == 0) return;
        GLib.Functions.SourceRemove(_countdown);
        _countdown = 0;
    }
}
