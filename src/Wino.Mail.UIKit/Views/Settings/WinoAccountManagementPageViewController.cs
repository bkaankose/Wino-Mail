using System.Windows.Input;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Wino Account (Windows WinoAccountManagementPage, shared WinoAccountManagementPageViewModel).
/// Signed out: the account header with Sign in / Create account, the four offers and the detail of
/// the selected one. Signed in: the profile card, the add-ons (Wino Intelligence, Unlimited Accounts,
/// the App Store redeem card) and Backup and restore. The card groups live in the partial files under
/// WinoAccount/. Opening the page with <see cref="WinoAccountManagementActivationReason.CheckoutCompleted"/>
/// forces a profile refresh in the ViewModel, exactly like Windows. The Mac also re-applies a billing return
/// to an open page (ISettingsPageParameterReceiver); the UIKit Settings sheet has no such hook yet.
/// </summary>
public sealed partial class WinoAccountManagementPageViewController(WinoAccountManagementPageViewModel viewModel, IPlatformCapabilities capabilities,
    IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoAccountManagementPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly IPlatformCapabilities _capabilities = capabilities;
    private readonly UIStackView _content = WinoLayout.VStack(WinoSettingsStyle.CardSpacing);
    private UIView? _loading;
    private UIActivityIndicatorView? _loadingSpinner;
    private bool _revealed;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // First load: the ViewModel starts signed out. Showing that would flash the offers before the
        // cached account state lands, so the content waits behind one spinner (Intelligence page pattern).
        _loading = Add(IntelligenceViews.LoadingState(out var loadingSpinner));
        _loadingSpinner = loadingSpinner;
        _content.Alignment = UIStackViewAlignment.Fill;
        _content.Hidden = true;
        Add(_content);

        // The network refresh row appears only when the refresh is slow, so a quick refresh does not shift the cards.
        var spinner = IntelligenceViews.Spinner();
        var updating = Row(spinner, WinoStyle.Label(Translator.WinoIntelligence_Updating, WinoStyle.Body, WinoStyle.PrimaryText));
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
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        // The parameter carries CheckoutCompleted when the billing return opens the page.
        try { await ViewModel.InitializeAsync(mode, parameter); }
        finally { await Dispatcher.ExecuteOnUIThread(Reveal); }
    }

    /// <summary>Replaces the first-load spinner with the page content, once.</summary>
    private void Reveal()
    {
        if (_revealed || Bindings.IsDisposed) return;
        _revealed = true;
        _loadingSpinner?.StopAnimating();
        if (_loading is not null) _loading.Hidden = true;
        _content.Hidden = false;
    }

    /// <summary>Adds a section to the content stack (hidden during the first load) at the content width.</summary>
    private T AddContent<T>(T view) where T : UIView => AddTo(_content, view);

    private static T AddTo<T>(UIStackView stack, T view) where T : UIView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        stack.AddArrangedSubview(view);
        return view;
    }

    /// <summary>A vertical stack whose children stretch to its width (one panel of the page).</summary>
    private static UIStackView Panel(double spacing = WinoSettingsStyle.CardSpacing)
    {
        var panel = WinoLayout.VStack(spacing);
        panel.Alignment = UIStackViewAlignment.Fill;
        return panel;
    }

    private WinoInfoBar Bar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message)
    {
        var bar = InfoBar(severity, null, null);
        Bind.Bind(ViewModel, property, message, text => { bar.Message = text; bar.Hidden = string.IsNullOrWhiteSpace(text); });
        return bar;
    }

    private WinoInfoBar CommandBar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message, string action, ICommand command)
    {
        var bar = Bar(severity, property, message);
        bar.ActionTitle = action;
        EventHandler handler = (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        bar.ActionInvoked += handler;
        Bindings.Own(new ActionDisposable(() => bar.ActionInvoked -= handler));
        return bar;
    }

    /// <summary>Windows SettingsSectionHeaderTextBlockStyle with its 22pt top margin.</summary>
    private static UIView SectionHeader(string text, UIView? trailing = null)
    {
        var label = WinoStyle.Label(text, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText, 0);
        label.AccessibilityTraits = UIAccessibilityTrait.Header;
        WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 250);
        var row = trailing is null ? WinoLayout.HStack(8, label) : WinoLayout.HStack(8, label, trailing);
        return WinoLayout.Insets(row, 18, 2, 4, 2);
    }

    /// <summary>A card surface like the Windows Border cards (card fill, card stroke).</summary>
    private static WinoSurfaceView Surface(UIView content, double padding, double radius)
        => IntelligenceViews.Surface(content, padding, radius);

    /// <summary>A rounded status pill (Active, Not active, Unlocked, Free, Add-on).</summary>
    private static UIView Pill(string text, UIColor textColor, UIColor fill, UIColor? stroke = null)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, textColor);
        var pill = new WinoSurfaceView { Fill = fill, Stroke = stroke, CornerRadius = 10, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, pill, 2, 10, 2, 10);
        WinoLayout.Priorities(pill, UILayoutConstraintAxis.Horizontal, 751, 1000);
        pill.IsAccessibilityElement = true;
        pill.AccessibilityTraits = UIAccessibilityTrait.StaticText;
        pill.AccessibilityLabel = text;
        return pill;
    }

    private static UIView SuccessPill(string text)
        => Pill(text, WinoStyle.Success, WinoStyle.Dynamic(WinoStyle.Hex(0xDFF6DD), WinoStyle.Hex(0x393D1B)));

    private static UIView NeutralPill(string text)
        => Pill(text, WinoStyle.SecondaryText, WinoSettingsStyle.SubtleFill, WinoSettingsStyle.CardStroke);

    /// <summary>An accent text button (Windows HyperlinkButton) bound to a command.</summary>
    private UIButton LinkButton(string title, ICommand command)
    {
        var button = IntelligenceViews.LinkButton(title);
        var binding = Bindings.Own(new CommandBinding(command, () => null, enabled => button.Enabled = enabled, Dispatcher, ReportError));
        Bind.OnActivated(button, binding.Execute);
        return button;
    }

    /// <summary>A spinner shown while <paramref name="read"/> is true.</summary>
    private UIActivityIndicatorView BusySpinner<T>(T source, string property, Func<T, bool> read) where T : System.ComponentModel.INotifyPropertyChanged
    {
        var spinner = IntelligenceViews.Spinner();
        Bind.Bind(source, property, read, on => IntelligenceViews.SetSpinning(spinner, on));
        return spinner;
    }
}
