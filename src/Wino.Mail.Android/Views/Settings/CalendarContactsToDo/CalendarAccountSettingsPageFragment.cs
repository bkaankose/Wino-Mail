using System.ComponentModel;
using System.Runtime.CompilerServices;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using View = Android.Views.View;

namespace Wino.Mail.Android.Views.Settings.CalendarContactsToDo;

/// <summary>
/// One calendar's settings (iOS and Mac CalendarAccountSettingsPageViewController, Windows CalendarAccountSettingsPage):
/// its colour, whether it synchronizes, and the default Show as status for new events. Every change persists through
/// <see cref="CalendarAccountSettingsPageViewModel"/>; a failed write shows an error bar with Retry. The navigation
/// parameter is the <c>AccountCalendar</c> or an account id (its primary calendar).
/// </summary>
public sealed class CalendarAccountSettingsPageFragment : SettingsPageFragment<CalendarAccountSettingsPageViewModel>, ISettingsPageTitleSource
{
    // The load runs with the page entry, not the view; the view (possibly recreated) watches it for the error bar.
    private static readonly ConditionalWeakTable<CalendarAccountSettingsPageViewModel, Task> Loads = new();

    private WinoInfoBar? _persistenceBar;
    private TextView? _intro;
    private Task? _observedPersistence;

    /// <summary>
    /// The iOS InitializeAsync: the awaitable load instead of the ViewModel's async void OnNavigatedTo, so load and
    /// first-save failures reach the error bar.
    /// </summary>
    public static Task InitializeAsync(CalendarAccountSettingsPageViewModel viewModel, NavigationMode mode, object? parameter)
    {
        var load = viewModel.InitializeNavigationAsync(mode, parameter);
        Loads.AddOrUpdate(viewModel, load);
        return load;
    }

    public string? PageTitle => Entry?.ViewModel is CalendarAccountSettingsPageViewModel { AccountCalendar.Name: { Length: > 0 } name }
        ? name
        : Translator.CalendarAccountSettings_Title;

    /// <summary>The intro names the calendar and its account instead of the catalogue line.</summary>
    protected override bool ShowsPageDescription => false;

    protected override void BuildPage()
    {
        var vm = VM;
        var context = Ctx;

        _intro = AddIntro(string.Empty);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => ApplyIntro());
        Bind.Bind(vm, nameof(vm.AccountCalendar), s => s.AccountCalendar, _ =>
        {
            ApplyIntro();
            UpdateTitle();
        });

        var bar = InfoBar(WinoInfoBarSeverity.Error, Translator.GeneralTitle_Error, null);
        bar.ActionTitle = Translator.Buttons_Retry;
        bar.IsClosable = true;
        bar.IsOpen = false;
        EventHandler retry = (_, _) => Retry();
        bar.ActionInvoked += retry;
        _persistenceBar = bar;
        Bindings.Own(new ActionDisposable(() =>
        {
            bar.ActionInvoked -= retry;
            _persistenceBar = null;
            _intro = null;
            _observedPersistence = null;
        }));
        Add(bar);

        // Colour: the Windows card shows the current colour; the swatches also let it change.
        var preview = new View(context) { ContentDescription = Translator.CalendarAccountSettings_AccountColor };
        preview.LayoutParameters = new FrameLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_small), WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_small));
        var swatches = new LinearLayout(context) { Orientation = Orientation.Horizontal, ContentDescription = Translator.CalendarAccountSettings_AccountColor };
        var swatchScroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
        swatchScroll.AddView(swatches);
        Bind.Bind(vm, nameof(vm.AccountColorHex), s => s.AccountColorHex, hex =>
        {
            var fill = new GradientDrawable();
            fill.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_radius_small));
            fill.SetColor(WinoColors.FromHexString(hex) ?? WinoColors.Accent(context));
            preview.Background = fill;
            AndroidX.Core.View.ViewCompat.SetStateDescription(preview, hex);
            BuildSwatches(swatches, hex);
        });
        var color = Card(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color, preview);
        color.BottomContent = swatchScroll;

        var sync = Card(Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
            Bind.Switch(vm, nameof(vm.IsSyncEnabled), s => s.IsSyncEnabled, (s, v) => s.IsSyncEnabled = v, Translator.CalendarAccountSettings_SyncEnabled));

        var showAs = Card(Translator.CalendarAccountSettings_DefaultShowAs, Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar,
            Bind.PopUp<CalendarAccountSettingsPageViewModel, ShowAsOption>(vm, s => s.ShowAsOptions.ToList(), option => option.DisplayText,
                nameof(vm.SelectedDefaultShowAsOption), s => s.SelectedDefaultShowAsOption, (s, v) => s.SelectedDefaultShowAsOption = v, width: Resource.Dimension.wino_size_field_narrow));

        var cards = AddGroup(null, color, sync, showAs);
        // Nothing to edit until the calendar has loaded (or when the parameter named no calendar).
        Bind.Bind(vm, nameof(vm.AccountCalendar), s => s.AccountCalendar is not null, loaded => SettingsBinder.SetEnabled(cards, loaded));

        vm.PropertyChanged += ViewModelChanged;
        Bindings.Own(new ActionDisposable(() => vm.PropertyChanged -= ViewModelChanged));

        if (Loads.TryGetValue(vm, out var load)) ObserveLoad(load);
        if (!vm.PendingPersistence.IsCompletedSuccessfully) ObservePersistence(vm.PendingPersistence);
    }

    /// <summary>The theme's account colours as round swatches, with the calendar's own colour first when it is not one of them.</summary>
    private void BuildSwatches(LinearLayout host, string? current)
    {
        var context = host.Context;
        if (context is null) return;
        host.RemoveAllViews();
        var colors = MainApplication.Services?.GetService<INewThemeService>()?.GetAvailableAccountColors() ?? [];
        if (!string.IsNullOrWhiteSpace(current) && !colors.Contains(current, StringComparer.OrdinalIgnoreCase)) colors.Insert(0, current);
        int size = WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_large);
        foreach (var hex in colors)
        {
            bool selected = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var swatch = new View(context) { ContentDescription = hex, Clickable = true, Focusable = true, Selected = selected };
            var fill = new GradientDrawable();
            fill.SetShape(ShapeType.Oval);
            fill.SetColor(WinoColors.FromHexString(hex) ?? WinoColors.Accent(context));
            if (selected) fill.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_calendar_swatch_selected_stroke), WinoColors.PrimaryText(context));
            swatch.Background = fill;
            var layout = new LinearLayout.LayoutParams(size, size);
            if (host.ChildCount > 0) layout.MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s);
            host.AddView(swatch, layout);
            Bind.Views.Click(swatch, () =>
            {
                var vm = VM;
                if (vm.AccountCalendar is null || string.Equals(vm.AccountColorHex, hex, StringComparison.OrdinalIgnoreCase)) return;
                vm.AccountColorHex = hex;
            });
        }
    }

    private void ApplyIntro()
    {
        if (_intro is null) return;
        var account = VM.Account;
        var calendar = VM.AccountCalendar;
        var accountText = account is null ? null : string.IsNullOrWhiteSpace(account.Address) ? account.Name : $"{account.Name} · {account.Address}";
        _intro.Text = accountText is null
            ? string.Empty
            : string.Format(Translator.CalendarAccountSettings_Description, calendar?.Name is { Length: > 0 } name ? $"{name} ({accountText})" : accountText);
        _intro.Visibility = string.IsNullOrEmpty(_intro.Text) ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>Each persisted property starts a write (PendingPersistence); watch the latest one.</summary>
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CalendarAccountSettingsPageViewModel.AccountColorHex)
            or nameof(CalendarAccountSettingsPageViewModel.IsSyncEnabled)
            or nameof(CalendarAccountSettingsPageViewModel.SelectedDefaultShowAsOption)
            && sender is CalendarAccountSettingsPageViewModel vm)
            ObservePersistence(vm.PendingPersistence);
    }

    private async void ObserveLoad(Task load)
    {
        try { await load; }
        catch (Exception exception)
        {
            ReportError(exception);
            await Dispatcher.ExecuteOnUIThread(() => ShowPersistenceError(exception));
        }
    }

    private async void ObservePersistence(Task persistence)
    {
        if (ReferenceEquals(_observedPersistence, persistence)) return;
        _observedPersistence = persistence;
        var dispatcher = Dispatcher;
        try
        {
            await persistence;
            if (ReferenceEquals(_observedPersistence, persistence))
                await dispatcher.ExecuteOnUIThread(() => { if (_persistenceBar is { } bar) bar.IsOpen = false; });
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_observedPersistence, persistence)) return;
            ReportError(exception);
            await dispatcher.ExecuteOnUIThread(() => ShowPersistenceError(exception));
        }
    }

    private void ShowPersistenceError(Exception exception)
    {
        if (_persistenceBar is not { } bar) return;
        bar.Message = exception.Message;
        bar.IsOpen = true;
    }

    private void Retry()
    {
        if (_persistenceBar is { } bar) bar.IsOpen = false;
        try { ObservePersistence(VM.RetryPersistenceAsync()); }
        catch (Exception exception) { ShowPersistenceError(exception); }
    }
}
