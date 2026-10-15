using System.ComponentModel;
using System.Windows.Input;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.Android.Common;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// Wino Account (Windows WinoAccountManagementPage, iOS WinoAccountManagementPageViewController; shared
/// WinoAccountManagementPageViewModel). Signed out: the account header with Sign in / Create account, the four offers
/// and the detail of the selected one. Signed in: the profile card, the add-ons (Wino Intelligence, Unlimited Accounts)
/// and the account management cards. The card groups live in the partial files beside this one. Android v1 sells
/// nothing in the app (no Play Billing; IPlatformCapabilities.Purchases is false), so the
/// purchase buttons, the price note and the store redeem card are absent; Backup and restore is not routed on Android
/// (IPlatformCapabilities.FileBackup is false), so its card is absent too. Opening the page with
/// WinoAccountManagementActivationReason.CheckoutCompleted forces a profile refresh in the ViewModel, as on Windows.
/// </summary>
public sealed partial class WinoAccountManagementPageFragment : SettingsPageFragment<WinoAccountManagementPageViewModel>
{
    private IPlatformCapabilities? _capabilities;
    private LinearLayout? _content;
    private View? _loading;
    private bool _revealed;

    /// <summary>The platform sells add-ons (the shared ViewModel's IPlatformCapabilities.Purchases rule).</summary>
    private bool SellsAddOns => ViewModel?.IsPurchaseAvailable == true;

    protected override void BuildPage()
    {
        var vm = VM;
        _capabilities = MainApplication.Services?.GetService<IPlatformCapabilities>();
        _revealed = false;

        // First load: the ViewModel starts signed out. Showing that would flash the offers before the cached account
        // state lands, so the content waits behind one spinner (Intelligence page pattern).
        _loading = Add(IntelligenceViews.LoadingState(Ctx));
        var content = Stack(Resource.Dimension.wino_space_2xs);
        content.Visibility = ViewStates.Gone;
        _content = content;
        Add(content);
        Bindings.Own(new ActionDisposable(() =>
        {
            _content = null;
            _loading = null;
        }));

        // The network refresh row appears only when the refresh is slow, so a quick refresh does not shift the cards.
        var spinner = IntelligenceViews.Spinner(Ctx);
        var updating = Row(spinner, Text(Translator.WinoIntelligence_Updating));
        var showUpdating = IntelligenceViews.DelayedProgressRow(updating, spinner, () => !Bindings.IsDisposed);
        Bind.Bind(vm, nameof(vm.IsIntelligenceRefreshing), s => s.IsIntelligenceRefreshing, on =>
        {
            // The refresh starts after the account state and the cached snapshot are applied.
            if (on) Reveal();
            showUpdating(on);
        });
        AddContent(updating);

        // Windows: the purchase status bar is not closable; the cached refresh error bar is.
        AddContent(CommandBar(WinoInfoBarSeverity.Informational, nameof(vm.PurchaseStatusMessage), s => s.PurchaseStatusMessage,
            Translator.WinoAccount_Management_RefreshPurchases, vm.RefreshPurchasesCommand));
        var refreshError = CommandBar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand);
        refreshError.IsClosable = true;
        AddContent(refreshError);

        AddContent(Bind.Visible(SignedOutPanel(), vm, nameof(vm.IsSignedIn), s => s.IsSignedOut));
        AddContent(Bind.Visible(SignedInPanel(), vm, nameof(vm.IsSignedIn), s => s.IsSignedIn));

        _ = RevealWhenLoadedAsync(vm);
    }

    /// <summary>The route's initializer; the parameter carries CheckoutCompleted when the billing return opens the page.</summary>
    internal static Task InitializeAsync(WinoAccountManagementPageViewModel viewModel, NavigationMode mode, object? parameter)
        => PageLoadTracker.TrackAsync(viewModel, () => viewModel.InitializeAsync(mode, parameter));

    private async Task RevealWhenLoadedAsync(WinoAccountManagementPageViewModel vm)
    {
        var scope = Bindings;
        try { await PageLoadTracker.WhenLoaded(vm); }
        catch (Exception exception) { ReportError(exception); }
        await Dispatcher.ExecuteOnUIThread(() => { if (!scope.IsDisposed) Reveal(); });
    }

    /// <summary>Replaces the first-load spinner with the page content, once per view.</summary>
    private void Reveal()
    {
        if (_revealed || Bindings.IsDisposed) return;
        _revealed = true;
        if (_loading is not null) _loading.Visibility = ViewStates.Gone;
        if (_content is not null) _content.Visibility = ViewStates.Visible;
    }

    /// <summary>Adds a section to the content stack (hidden during the first load) at the content width.</summary>
    private T AddContent<T>(T view) where T : View
    {
        if (_content is { } content) AddTo(content, view);
        return view;
    }

    /// <summary>A vertical stack whose children stretch to its width (one panel of the page).</summary>
    private LinearLayout Panel(int spacing = Resource.Dimension.wino_space_2xs) => Stack(spacing);

    private WinoInfoBar Bar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message)
    {
        var bar = InfoBar(severity, null, null);
        Bind.Bind(VM, property, message, text =>
        {
            bar.Message = text;
            bar.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
        });
        return bar;
    }

    private WinoInfoBar CommandBar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message, string action, ICommand command)
    {
        var bar = Bar(severity, property, message);
        bar.ActionTitle = action;
        EventHandler handler = (_, _) =>
        {
            try { if (command.CanExecute(null)) command.Execute(null); }
            catch (Exception exception) { ReportError(exception); }
        };
        bar.ActionInvoked += handler;
        Bindings.Own(new ActionDisposable(() => bar.ActionInvoked -= handler));
        return bar;
    }

    /// <summary>Windows SettingsSectionHeaderTextBlockStyle with its top margin, and an optional trailing control.</summary>
    private View SectionHeader(string text, View? trailing = null)
    {
        var label = Text(text, WinoTextStyle.BodyLargeStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) label.AccessibilityHeading = true;
        var row = trailing is null
            ? IntelligenceViews.HStack(Ctx, Resource.Dimension.wino_space_s, label, label)
            : IntelligenceViews.HStack(Ctx, Resource.Dimension.wino_space_s, label, label, trailing);
        WinoTokens.Pad(row, Resource.Dimension.wino_space_3xs, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_3xs, Resource.Dimension.wino_space_2xs);
        return row;
    }

    /// <summary>A card surface like the Windows Border cards (card fill, card stroke).</summary>
    private FrameLayout Surface(View content, int padding, int radius) => IntelligenceViews.Surface(Ctx, content, padding, radius);

    /// <summary>An accent text button (Windows HyperlinkButton) bound to a command.</summary>
    private MaterialButton LinkButton(string title, ICommand command)
        => Bind.Views.Command(IntelligenceViews.LinkButton(Ctx, title), command);

    /// <summary>A spinner shown while <paramref name="read"/> is true.</summary>
    private View BusySpinner<T>(T source, string property, Func<T, bool> read) where T : INotifyPropertyChanged
    {
        var spinner = IntelligenceViews.Spinner(Ctx);
        Bind.Bind(source, property, read, on => IntelligenceViews.SetSpinning(spinner, on));
        return spinner;
    }
}
