using System.ComponentModel;
using AppKit;
#if DEBUG
using Microsoft.Extensions.DependencyInjection;
#endif
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Shell;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// One calendar's settings (Windows CalendarAccountSettingsPage): its colour, whether it
/// synchronizes, and the default Show as status for new events. Every change persists through
/// <see cref="CalendarAccountSettingsPageViewModel"/>; a failed write shows an error bar with Retry.
/// The navigation parameter is the <c>AccountCalendar</c> or an account id (its primary calendar).
/// </summary>
public sealed class CalendarAccountSettingsPageViewController(CalendarAccountSettingsPageViewModel viewModel, INewThemeService themes, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<CalendarAccountSettingsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private WinoInfoBar _persistenceBar = null!;
    private NSTextField _intro = null!;
    private Task? _observedPersistence;

    public string? PageTitle => ViewModel.AccountCalendar?.Name is { Length: > 0 } name ? name : Translator.CalendarAccountSettings_Title;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
#if DEBUG
        CalendarAccountSettingsEntryPoints.Current = new WeakReference<CalendarAccountSettingsPageViewController>(this);
#endif

        _intro = AddIntro(string.Empty);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => ApplyIntro());
        Bind.Bind(vm, nameof(vm.AccountCalendar), s => s.AccountCalendar, _ =>
        {
            ApplyIntro();
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });

        _persistenceBar = InfoBar(WinoInfoBarSeverity.Error, Translator.GeneralTitle_Error, null);
        _persistenceBar.ActionTitle = Translator.Buttons_Retry;
        _persistenceBar.IsClosable = true;
        _persistenceBar.Hidden = true;
        EventHandler retry = (_, _) => Retry();
        _persistenceBar.ActionInvoked += retry;
        Bindings.Own(new ActionDisposable(() => _persistenceBar.ActionInvoked -= retry));
        Add(_persistenceBar);

        // Colour: the Windows card shows the current colour; the swatches also let it change.
        var swatches = new WinoColorSwatchPicker();
        swatches.WidthAnchor.ConstraintEqualTo(260).Active = true;
        var preview = new WinoSurfaceView { CornerRadius = 4 };
        WinoLayout.Size(preview, 24, 24);
        WinoAccessibility.Label(preview, Translator.CalendarAccountSettings_AccountColor);
        Bind.Bind(vm, nameof(vm.AccountColorHex), s => s.AccountColorHex, hex =>
        {
            swatches.Colors = Palette(hex);
            swatches.SelectedHex = hex;
            preview.Fill = WinoStyle.FromHexString(hex) ?? WinoStyle.Accent;
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
                nameof(vm.SelectedDefaultShowAsOption), s => s.SelectedDefaultShowAsOption, (s, v) => s.SelectedDefaultShowAsOption = v, width: 180));

        var cards = AddGroup(null, color, sync, showAs);
        // Nothing to edit until the calendar has loaded (or when the parameter named no calendar).
        Bind.Bind(vm, nameof(vm.AccountCalendar), s => s.AccountCalendar is not null, loaded => SettingsBinder.SetEnabled(cards, loaded));

        vm.PropertyChanged += ViewModelChanged;
        Bindings.Own(new ActionDisposable(() => vm.PropertyChanged -= ViewModelChanged));
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // The ViewModel's async void OnNavigatedTo would hide load and first-save failures; await the task form.
        try { await ViewModel.InitializeNavigationAsync(mode, parameter!); }
        catch (Exception exception)
        {
            ReportError(exception);
            await Dispatcher.ExecuteOnUIThread(() => ShowPersistenceError(exception));
        }
    }

    private void ApplyIntro()
    {
        var account = ViewModel.Account;
        var calendar = ViewModel.AccountCalendar;
        var accountText = account is null ? null : string.IsNullOrWhiteSpace(account.Address) ? account.Name : $"{account.Name} · {account.Address}";
        _intro.StringValue = accountText is null
            ? string.Empty
            : string.Format(Translator.CalendarAccountSettings_Description, calendar?.Name is { Length: > 0 } name ? $"{name} ({accountText})" : accountText);
        _intro.Hidden = string.IsNullOrEmpty(_intro.StringValue);
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

#if DEBUG
    /// <summary>Debug bridge: the loaded calendar and its edited values.</summary>
    internal string Describe()
        => $"calendar={ViewModel.AccountCalendar?.Name} account={ViewModel.Account?.Address} color={ViewModel.AccountColorHex} sync={ViewModel.IsSyncEnabled} " +
           $"showAs={ViewModel.SelectedDefaultShowAsOption?.ShowAs} error={(_persistenceBar.Hidden ? "none" : _persistenceBar.Message)}";
#endif
}

/// <summary>
/// Ways into <see cref="CalendarAccountSettingsPageViewController"/>: the shell pane's calendar rows get
/// a context menu entry (through <see cref="ShellSidebarContextMenus.AddExtension"/>), and an account's
/// calendar group header opens that account's Calendar tab. Registered once with the views.
/// </summary>
internal static class CalendarAccountSettingsEntryPoints
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        ShellSidebarContextMenus.AddExtension(Items);
#if DEBUG
        RegisterDebugCommands();
#endif
    }

    private static IEnumerable<NSMenuItem> Items(ShellSidebarMenuTarget target, ShellSidebarMenuContext context)
    {
        if (target.Calendar?.AccountCalendar is { } calendar)
        {
            yield return ShellSidebarContextMenus.Item($"{Translator.CalendarAccountSettings_Title}…", WinoIconGlyph.Settings,
                () => Navigate(context, WinoPage.CalendarAccountSettingsPage, calendar), context);
        }
        else if (target.Item is AccountCalendarGroupMenuItem { Parameter.Account: { } account })
        {
            yield return ShellSidebarContextMenus.Item(Translator.AccountContextMenu_ManageAccountSettings, WinoIconGlyph.ManageAccounts,
                () => Navigate(context, WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(account.Id, AccountDetailsTab.Calendar)), context);
        }
    }

    private static Task Navigate(ShellSidebarMenuContext context, WinoPage page, object parameter)
    {
        context.Navigation.Navigate(page, parameter);
        return Task.CompletedTask;
    }

#if DEBUG
    /// <summary>The settings page most recently built, for the debug bridge.</summary>
    internal static WeakReference<CalendarAccountSettingsPageViewController>? Current { get; set; }

    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    /// <summary>
    /// "calaccount [CALENDAR-ID]" opens the calendar's settings page (an account id opens its primary
    /// calendar; no id opens the first calendar) and reports the loaded values.
    /// </summary>
    private static void RegisterDebugCommands()
    {
        MacDebugBridge.Register("calaccount", async args =>
        {
            var calendars = Services.GetRequiredService<ICalendarService>();
            object? parameter = null;
            if (args.Length > 0)
            {
                if (!Guid.TryParse(args[0], out var id)) return "usage: calaccount [CALENDAR-ID]";
                parameter = await calendars.GetAccountCalendarAsync(id) is { } calendar ? calendar : (object)id;
            }
            else
            {
                foreach (var account in await Services.GetRequiredService<IAccountService>().GetAccountsAsync())
                {
                    var first = (await calendars.GetAccountCalendarsAsync(account.Id)).FirstOrDefault();
                    if (first is not null) { parameter = first; break; }
                }
            }
            if (parameter is null) return "no calendars";
            Current = null;
            if (!Services.GetRequiredService<AppKitNavigationService>().Navigate(WinoPage.CalendarAccountSettingsPage, parameter)) return "refused";
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100);
                if (Current?.TryGetTarget(out var page) == true && page.Describe() is var state && !state.StartsWith("calendar= ", StringComparison.Ordinal)) return state;
            }
            return Current?.TryGetTarget(out var shown) == true ? shown.Describe() : "page did not open";
        });
    }
#endif
}
