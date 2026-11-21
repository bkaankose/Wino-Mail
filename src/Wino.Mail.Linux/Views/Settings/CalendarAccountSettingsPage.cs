using System.ComponentModel;
using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// One calendar's settings (Windows CalendarAccountSettingsPage): colour, sync and the default show-as. Every edit
/// starts a write (<see cref="CalendarAccountSettingsPageViewModel.PendingPersistence"/>); a failed write shows a
/// banner with Retry. Parameter: an <c>AccountCalendar</c>, or an account id for its primary calendar.
/// </summary>
internal sealed class CalendarAccountSettingsPage(CalendarAccountSettingsPageViewModel viewModel, WinoViewServices services, INewThemeService themes)
    : SettingsPageView<CalendarAccountSettingsPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private Adw.Banner? _error;
    private Task? _observedPersistence;

    public string? PageTitle => Translator.CalendarAccountSettings_Title;

    public string? PageDescription
    {
        get
        {
            var account = ViewModel.Account;
            if (account is null) return null;
            var accountText = string.IsNullOrWhiteSpace(account.Address) ? account.Name : $"{account.Name} · {account.Address}";
            var calendar = ViewModel.AccountCalendar?.Name;
            return string.Format(CultureInfo.CurrentCulture, Translator.CalendarAccountSettings_Description,
                string.IsNullOrEmpty(calendar) ? accountText : $"{calendar} ({accountText})");
        }
    }

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));
        Settings.Bind(vm, nameof(vm.AccountCalendar), static s => s.AccountCalendar, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var error = _error = Page.AddBanner(Translator.GeneralTitle_Error);
        error.SetButtonLabel(Translator.Buttons_Retry);
        Settings.Own(GtkSignals.Connect<Adw.Banner>(h => error.OnButtonClicked += h, h => error.OnButtonClicked -= h, (_, _) => Retry()));

        var swatches = new WinoColorSwatches(Translator.CalendarAccountSettings_AccountColor);
        Settings.Own(new ActionDisposable(swatches.Release));
        Settings.Bind(vm, nameof(vm.AccountColorHex), static s => s.AccountColorHex, hex =>
        {
            swatches.Colors = Palette(hex);
            swatches.SelectedHex = hex;
        });
        swatches.SelectionChanged += hex =>
        {
            if (vm.AccountCalendar is null || string.Equals(vm.AccountColorHex, hex, StringComparison.OrdinalIgnoreCase)) return;
            vm.AccountColorHex = hex;
        };

        var sync = Settings.Switch(vm, Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
            nameof(vm.IsSyncEnabled), static s => s.IsSyncEnabled, static (s, v) => s.IsSyncEnabled = v);
        Settings.Visible(sync, vm, nameof(vm.IsSyncConfigurable), static s => s.IsSyncConfigurable);

        var group = AddGroup(null,
            WinoSettingsRows.Action(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color),
            WinoSettingsRows.Custom(swatches.Root, Translator.CalendarAccountSettings_AccountColor),
            sync,
            Settings.Choice<CalendarAccountSettingsPageViewModel, ShowAsOption>(vm, Translator.CalendarAccountSettings_DefaultShowAs, Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar,
                static s => s.ShowAsOptions, static o => o.DisplayText,
                nameof(vm.SelectedDefaultShowAsOption), static s => s.SelectedDefaultShowAsOption, static (s, v) => s.SelectedDefaultShowAsOption = v,
                itemsCollection: vm.ShowAsOptions));
        // Nothing to edit until the calendar has loaded (or when the parameter named no calendar).
        Settings.Bind(vm, nameof(vm.AccountCalendar), static s => s.AccountCalendar is not null, group.Root.SetSensitive);

        PropertyChangedEventHandler changed = ViewModelChanged;
        vm.PropertyChanged += changed;
        Settings.Own(new ActionDisposable(() => vm.PropertyChanged -= changed));
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        // The ViewModel's async void OnNavigatedTo would hide load and first-save failures; await the task form.
        try { await ViewModel.InitializeNavigationAsync(mode, parameter); }
        catch (Exception exception)
        {
            ReportError(exception);
            RunOnUI(() => ShowError(exception));
        }
    }

    /// <summary>The theme's account colours, with the calendar's own colour first when it is not one of them.</summary>
    private List<string> Palette(string? current)
    {
        var colors = themes.GetAvailableAccountColors();
        if (!string.IsNullOrWhiteSpace(current) && !colors.Contains(current, StringComparer.OrdinalIgnoreCase)) colors.Insert(0, current);
        return colors;
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ViewModel.AccountColorHex) or nameof(ViewModel.IsSyncEnabled) or nameof(ViewModel.SelectedDefaultShowAsOption))
            ObservePersistence(ViewModel.PendingPersistence);
    }

    private async void ObservePersistence(Task persistence)
    {
        if (ReferenceEquals(_observedPersistence, persistence)) return;
        _observedPersistence = persistence;
        try
        {
            await persistence;
            if (ReferenceEquals(_observedPersistence, persistence)) RunOnUI(() => _error?.SetRevealed(false));
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_observedPersistence, persistence)) return;
            ReportError(exception);
            RunOnUI(() => ShowError(exception));
        }
    }

    private void ShowError(Exception exception)
    {
        if (_error is null) return;
        _error.SetTitle($"{Translator.GeneralTitle_Error}: {exception.Message}");
        _error.SetRevealed(true);
    }

    private void Retry()
    {
        _error?.SetRevealed(false);
        try { ObservePersistence(ViewModel.RetryPersistenceAsync()); }
        catch (Exception exception) { ShowError(exception); }
    }
}
