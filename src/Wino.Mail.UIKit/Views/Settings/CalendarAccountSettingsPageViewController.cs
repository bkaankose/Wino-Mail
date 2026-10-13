using System.ComponentModel;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// One calendar's settings (Mac CalendarAccountSettingsPageViewController, Windows CalendarAccountSettingsPage): its
/// colour, whether it synchronizes, and the default Show as status for new events. Every change persists through
/// <see cref="CalendarAccountSettingsPageViewModel"/>; a failed write shows an error bar with Retry. The navigation
/// parameter is the <c>AccountCalendar</c> or an account id (its primary calendar).
/// </summary>
public sealed class CalendarAccountSettingsPageViewController(CalendarAccountSettingsPageViewModel viewModel, INewThemeService themes, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<CalendarAccountSettingsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoInfoBar _persistenceBar = new(WinoInfoBarSeverity.Error, Translator.GeneralTitle_Error, null);
    private UILabel? _intro;
    private Task? _observedPersistence;

    public string? PageTitle => ViewModel.AccountCalendar?.Name is { Length: > 0 } name ? name : Translator.CalendarAccountSettings_Title;
    public event EventHandler? PageTitleChanged;

    /// <summary>The intro names the calendar and its account instead of the catalog line.</summary>
    protected override bool ShowsPageDescription => false;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        _intro = AddIntro(string.Empty);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => ApplyIntro());
        Bind.Bind(vm, nameof(vm.AccountCalendar), s => s.AccountCalendar, _ =>
        {
            ApplyIntro();
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });

        _persistenceBar.ActionTitle = Translator.Buttons_Retry;
        _persistenceBar.IsClosable = true;
        _persistenceBar.Hidden = true;
        EventHandler retry = (_, _) => Retry();
        _persistenceBar.ActionInvoked += retry;
        Bindings.Own(new ActionDisposable(() => _persistenceBar.ActionInvoked -= retry));
        Add(_persistenceBar);

        // Colour: the Windows card shows the current colour; the swatches also let it change.
        var swatches = new WinoColorSwatchPicker();
        WinoAccessibility.Label(swatches, Translator.CalendarAccountSettings_AccountColor);
        var preview = new WinoSurfaceView { CornerRadius = 4 };
        WinoLayout.Size(preview, 24, 24);
        WinoAccessibility.Label(preview, Translator.CalendarAccountSettings_AccountColor);
        Bind.Bind(vm, nameof(vm.AccountColorHex), s => s.AccountColorHex, hex =>
        {
            swatches.Colors = Palette(hex);
            swatches.SelectedHex = hex;
            preview.Fill = WinoStyle.FromHexString(hex) ?? WinoStyle.Accent;
            preview.AccessibilityValue = hex;
        });
        EventHandler<string> picked = (_, hex) =>
        {
            if (vm.AccountCalendar is null || string.Equals(vm.AccountColorHex, hex, StringComparison.OrdinalIgnoreCase)) return;
            vm.AccountColorHex = hex;
        };
        swatches.SelectionChanged += picked;
        Bindings.Own(new ActionDisposable(() => swatches.SelectionChanged -= picked));
        var color = Card(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color, preview);
        color.BottomContent = swatches;

        var sync = Card(Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
            Bind.Switch(vm, nameof(vm.IsSyncEnabled), s => s.IsSyncEnabled, (s, v) => s.IsSyncEnabled = v, Translator.CalendarAccountSettings_SyncEnabled));

        var showAs = Card(Translator.CalendarAccountSettings_DefaultShowAs, Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar,
            Bind.PopUp<CalendarAccountSettingsPageViewModel, ShowAsOption>(vm, s => s.ShowAsOptions.ToList(), option => option.DisplayText,
                nameof(vm.SelectedDefaultShowAsOption), s => s.SelectedDefaultShowAsOption, (s, v) => s.SelectedDefaultShowAsOption = v, width: 120));

        var cards = AddGroup(null, color, sync, showAs);
        // Nothing to edit until the calendar has loaded (or when the parameter named no calendar).
        Bind.Bind(vm, nameof(vm.AccountCalendar), s => s.AccountCalendar is not null, loaded => SettingsBinder.SetEnabled(cards, loaded));

        vm.PropertyChanged += ViewModelChanged;
        Bindings.Own(new ActionDisposable(() => vm.PropertyChanged -= ViewModelChanged));
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // The ViewModel's async void OnNavigatedTo would hide load and first-save failures; await the task form.
        try { await ViewModel.InitializeNavigationAsync(mode, parameter); }
        catch (Exception exception)
        {
            ReportError(exception);
            await Dispatcher.ExecuteOnUIThread(() => ShowPersistenceError(exception));
        }
    }

    private void ApplyIntro()
    {
        if (_intro is null) return;
        var account = ViewModel.Account;
        var calendar = ViewModel.AccountCalendar;
        var accountText = account is null ? null : string.IsNullOrWhiteSpace(account.Address) ? account.Name : $"{account.Name} · {account.Address}";
        _intro.Text = accountText is null
            ? string.Empty
            : string.Format(Translator.CalendarAccountSettings_Description, calendar?.Name is { Length: > 0 } name ? $"{name} ({accountText})" : accountText);
        _intro.Hidden = string.IsNullOrEmpty(_intro.Text);
    }

    /// <summary>The theme's account colours, with the calendar's own colour first when it is not one of them.</summary>
    private IReadOnlyList<string> Palette(string? current)
    {
        var colors = themes.GetAvailableAccountColors().ToList();
        if (!string.IsNullOrWhiteSpace(current) && !colors.Contains(current, StringComparer.OrdinalIgnoreCase)) colors.Insert(0, current);
        return colors;
    }

    /// <summary>Each persisted property starts a write (PendingPersistence); watch the latest one.</summary>
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
            if (ReferenceEquals(_observedPersistence, persistence)) await Dispatcher.ExecuteOnUIThread(() => _persistenceBar.Hidden = true);
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_observedPersistence, persistence)) return;
            ReportError(exception);
            await Dispatcher.ExecuteOnUIThread(() => ShowPersistenceError(exception));
        }
    }

    private void ShowPersistenceError(Exception exception)
    {
        _persistenceBar.Message = exception.Message;
        _persistenceBar.Hidden = false;
    }

    private void Retry()
    {
        _persistenceBar.Hidden = true;
        try { ObservePersistence(ViewModel.RetryPersistenceAsync()); }
        catch (Exception exception) { ShowPersistenceError(exception); }
    }
}
