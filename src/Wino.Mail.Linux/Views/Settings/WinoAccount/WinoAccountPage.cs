using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.WinoAccount;

/// <summary>
/// Wino Account (Windows WinoAccountManagementPage, macOS WinoAccountManagementPageViewController, shared
/// WinoAccountManagementPageViewModel). Signed out: the account header with Sign in / Create account, the four offers and
/// the detail of the selected one. Signed in: the profile card, the add-ons and the management rows. Both states live
/// for the page's lifetime and are switched by visibility. The checkout return refreshes the purchases in place.
/// </summary>
internal sealed partial class WinoAccountPage(WinoAccountManagementPageViewModel viewModel, WinoViewServices services, IPlatformCapabilities capabilities)
    : SettingsPageView<WinoAccountManagementPageViewModel>(viewModel, services), ISettingsPageParameterReceiver
{
    private readonly IPlatformCapabilities _capabilities = capabilities;
    private readonly List<WinoSettingsGroup> _content = [];
    private WinoSettingsGroup? _loading;
    private bool _revealed;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // First load: the ViewModel starts signed out, so the content waits behind one spinner until the cached state lands.
        var loading = _loading = Page.AddGroup();
        loading.HidesWhenEmpty = false;
        var spinner = Adw.Spinner.New();
        spinner.SetSizeRequest(32, 32);
        spinner.SetMarginTop(48);
        loading.Root.Add(spinner);

        var bars = Content(Page.AddGroup());
        bars.HidesWhenEmpty = false;
        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        var updating = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        updating.Append(Adw.Spinner.New());
        updating.Append(Gtk.Label.New(Translator.WinoIntelligence_Updating));
        Settings.Visible(updating, vm, nameof(vm.IsIntelligenceRefreshing), static s => s.IsIntelligenceRefreshing);
        Settings.Bind(vm, nameof(vm.IsIntelligenceRefreshing), static s => s.IsIntelligenceRefreshing, on =>
        {
            if (on) Reveal();
        });
        stack.Append(updating);
        stack.Append(CommandBar(WinoInfoBarSeverity.Informational, nameof(vm.PurchaseStatusMessage), static s => s.PurchaseStatusMessage,
            vm.RefreshPurchasesText, vm.RefreshPurchasesCommand, closable: false).Root);
        stack.Append(CommandBar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), static s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand, closable: true).Root);
        bars.Root.Add(stack);

        foreach (var group in SignedOutGroups()) Content(group);
        foreach (var group in SignedInGroups()) Content(group);
        foreach (var group in _content) group.Root.SetVisible(false);
        Settings.Bind(vm, nameof(vm.IsSignedIn), static s => s.IsSignedIn, _ => ApplyRevealed());
#if DEBUG
        Current = this;
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        // The parameter carries CheckoutCompleted when the billing return (wino://billing/success) opens the page.
        try { await ViewModel.InitializeAsync(mode, parameter!); }
        finally { RunOnUI(Reveal); }
    }

    /// <summary>The billing return while this page is open: the ViewModel reruns its checkout handling here.</summary>
    public async Task<bool> ReceiveParameterAsync(object parameter)
    {
        if (parameter is not WinoAccountManagementActivationReason.CheckoutCompleted || IsReleased) return false;
        await ViewModel.InitializeAsync(NavigationMode.Refresh, parameter);
        return true;
    }

    protected override Task DeactivateAsync()
    {
        ReleaseSignedOut();
#if DEBUG
        if (ReferenceEquals(Current, this)) Current = null;
#endif
        return base.DeactivateAsync();
    }

    private WinoSettingsGroup Content(WinoSettingsGroup group)
    {
        _content.Add(group);
        return group;
    }

    private void Reveal()
    {
        if (_revealed || IsReleased) return;
        _revealed = true;
        _loading?.Root.SetVisible(false);
        ApplyRevealed();
    }

    /// <summary>Until the first state is known every content group stays hidden; then the signed-in flag picks the groups.</summary>
    private void ApplyRevealed()
    {
        if (!_revealed) return;
        var signedIn = ViewModel.IsSignedIn;
        for (var index = 0; index < _content.Count; index++)
        {
            var group = _content[index];
            group.Root.SetVisible(index == 0 || (_signedInGroups.Contains(group) ? signedIn : !signedIn));
        }
    }

    private WinoInfoBar CommandBar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message,
        string action, System.Windows.Input.ICommand command, bool closable)
    {
        var bar = new WinoInfoBar(severity) { ActionTitle = action, IsClosable = closable };
        Settings.Own(bar);
        bar.ActionInvoked += _ =>
        {
            if (command.CanExecute(null)) command.Execute(null);
        };
        Settings.Bind(ViewModel, property, message, text =>
        {
            bar.Message = text ?? string.Empty;
            bar.IsOpen = !string.IsNullOrWhiteSpace(text);
        });
        return bar;
    }

    private static Gtk.Label Text(string text, params string[] classes)
    {
        var label = Gtk.Label.New(text);
        label.SetXalign(0);
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        foreach (var css in classes) label.AddCssClass(css);
        return label;
    }

    /// <summary>A rounded status pill (Active, Ended, Unlocked).</summary>
    private static Gtk.Widget Pill(string text, bool success)
    {
        var label = Gtk.Label.New(text);
        label.AddCssClass("caption");
        label.AddCssClass(success ? "success" : "dim-label");
        var frame = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        frame.AddCssClass("card");
        label.SetMarginStart(10);
        label.SetMarginEnd(10);
        label.SetMarginTop(2);
        label.SetMarginBottom(2);
        frame.Append(label);
        frame.SetValign(Gtk.Align.Center);
        WinoA11y.Label(frame, text);
        return frame;
    }

#if DEBUG
    internal static WinoAccountPage? Current { get; private set; }

    internal string Describe()
    {
        var vm = ViewModel;
        var groups = string.Join(",", _content.Select(static g => g.Root.GetVisible() ? "1" : "0"));
        return $"signedIn={vm.IsSignedIn} busy={vm.IsBusy} revealed={_revealed} purchaseAvailable={vm.IsPurchaseAvailable} purchaseStatus='{vm.PurchaseStatusMessage}' "
            + $"aiPack='{vm.AiPackStatusText}' unlimited='{vm.UnlimitedAccountsSubtitleText}' usage='{vm.AccountUsageText}' selected={vm.SelectedBenefit?.Type} "
            + $"tiles={DescribeTiles()} groups={groups}";
    }
#endif
}
