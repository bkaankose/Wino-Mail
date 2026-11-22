using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// App lock (Windows AppLockPage): the hint, the PIN group (new PIN, repeat, error, Turn on / Change PIN and Turn off)
/// and read-only notifications. Linux unlocks with a Wino Mail PIN only, so there is no method picker.
/// </summary>
internal sealed class AppLockPage(AppLockPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<AppLockPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    public string? PageTitle => null;

    public string? PageDescription => Translator.Linux_AppLock_Description;

    event EventHandler? ISettingsPageTitleSource.PageTitleChanged { add { } remove { } }

    private Adw.PasswordEntryRow? _newPin;
    private Adw.PasswordEntryRow? _confirmPin;

    internal Adw.PasswordEntryRow? NewPinRow => _newPin;

    internal Adw.PasswordEntryRow? ConfirmPinRow => _confirmPin;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var unsupported = Page.AddBanner(Translator.AppLock_NotSupported);
        Settings.Bind(vm, nameof(vm.IsNotSupported), static s => s.IsNotSupported, unsupported.SetRevealed);
        Page.AddGroup(null, Translator.Linux_AppLock_Hint).HidesWhenEmpty = false;

        var newPin = _newPin = WinoSettingsRows.Password(Translator.AppLock_PinPlaceholder);
        var confirmPin = _confirmPin = WinoSettingsRows.Password(Translator.AppLock_PinConfirmPlaceholder);
        TwoWay(newPin, nameof(vm.NewPin), static s => s.NewPin, static (s, value) => s.NewPin = value);
        TwoWay(confirmPin, nameof(vm.ConfirmPin), static s => s.ConfirmPin, static (s, value) => s.ConfirmPin = value);
        confirmPin.SetActivatesDefault(false);
        Settings.Own(GtkSignals.Connect<Adw.EntryRow>(h => newPin.OnEntryActivated += h, h => newPin.OnEntryActivated -= h, (_, _) => confirmPin.GrabFocus()));
        Settings.Own(GtkSignals.Connect<Adw.EntryRow>(h => confirmPin.OnEntryActivated += h, h => confirmPin.OnEntryActivated -= h,
            (_, _) => Observe(ViewModel.ApplyCommand.ExecuteAsync(null))));

        var error = WinoSettingsRows.Caption(string.Empty);
        error.AddCssClass("error");
        error.RemoveCssClass("dim-label");
        Settings.Bind(vm, nameof(vm.ErrorText), static s => s.ErrorText, text => error.SetText(text ?? string.Empty));
        Settings.Visible(error, vm, nameof(vm.HasError), static s => s.HasError);

        var apply = Settings.Button(Translator.AppLock_TurnOn, () => Observe(ViewModel.ApplyCommand.ExecuteAsync(null)), suggested: true);
        Settings.Bind(vm, nameof(vm.ApplyText), static s => s.ApplyText, text =>
        {
            apply.SetLabel(text ?? Translator.AppLock_TurnOn);
            WinoA11y.Rename(apply, text ?? Translator.AppLock_TurnOn);
        });
        Settings.Enabled(apply, vm, nameof(vm.CanApply), static s => s.CanApply);
        var turnOff = Settings.Button(Translator.AppLock_TurnOff, () => Observe(ViewModel.TurnOffCommand.ExecuteAsync(null)), destructive: true);
        Settings.Visible(turnOff, vm, nameof(vm.IsLockOn), static s => s.IsLockOn);
        Settings.Enabled(turnOff, vm, nameof(vm.CanTurnOff), static s => s.CanTurnOff);
        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.SetHalign(Gtk.Align.Start);
        buttons.SetMarginTop(12);
        buttons.Append(apply);
        buttons.Append(turnOff);

        var pinGroup = Page.AddGroup(Translator.AppLock_PinEntryHeader, Translator.AppLock_PinDescription);
        pinGroup.Add(newPin);
        pinGroup.Add(confirmPin);
        var footer = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        footer.SetMarginTop(6);
        footer.Append(error);
        footer.Append(buttons);
        pinGroup.Add(WinoSettingsRows.Custom(footer, Translator.AppLock_PinEntryHeader));
        Settings.Visible(pinGroup.Root, vm, nameof(vm.IsPinEntryVisible), static s => s.IsPinEntryVisible);

        var readOnly = Settings.Switch(vm, Translator.AppLock_ReadOnlyNotificationsHeader, Translator.AppLock_ReadOnlyNotificationsDescription,
            WinoIconGlyph.Alert, nameof(vm.AreNotificationsReadOnly), static s => s.AreNotificationsReadOnly, static (s, value) => s.AreNotificationsReadOnly = value);
        var notifications = Page.AddGroup(Translator.AppLock_NotificationsHeader, null, readOnly);
        Settings.Visible(notifications.Root, vm, nameof(vm.IsLockOn), static s => s.IsLockOn);
    }

    /// <summary>A PIN row writes the ViewModel as the user types and follows it when the ViewModel clears the PINs.</summary>
    private void TwoWay(Adw.PasswordEntryRow row, string property, Func<AppLockPageViewModel, string?> read, Action<AppLockPageViewModel, string> write)
    {
        var applying = false;
        Settings.Bind(ViewModel, property, read, value =>
        {
            if (string.Equals(row.GetText(), value ?? string.Empty, StringComparison.Ordinal)) return;
            applying = true;
            try { row.SetText(value ?? string.Empty); }
            finally { applying = false; }
        });
        Settings.Own(GtkSignals.OnChanged(row, () =>
        {
            if (!applying && !IsReleased) write(ViewModel, row.GetText());
        }));
    }
}
