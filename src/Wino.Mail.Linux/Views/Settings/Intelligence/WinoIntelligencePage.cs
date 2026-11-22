using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>
/// Wino Intelligence home (Windows WinoIntelligencePage, macOS WinoIntelligencePageViewController, shared
/// WinoAccountManagementPageViewModel). Without access: the hero, "What's included" and the locked features. With
/// access: the status expander (subscription and usage), mail classification, languages and the AI provider access
/// switch. Both panels live for the page's lifetime and are switched by <c>HasIntelligenceAccess</c>.
/// </summary>
internal sealed partial class WinoIntelligencePage(WinoAccountManagementPageViewModel viewModel, WinoViewServices services,
    IMailDialogService dialogs, IExternalLauncher launcher)
    : SettingsPageView<WinoAccountManagementPageViewModel>(viewModel, services)
{
    private readonly List<WinoSettingsGroup> _content = [];
    private readonly IMailDialogService _dialogs = dialogs;
    private readonly IExternalLauncher _launcher = launcher;
    private WinoSettingsGroup? _loading;
    private bool _revealed;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // First load: the ViewModel starts signed out without access, so the offer would flash before the cached state lands.
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
        stack.Append(Bar(WinoInfoBarSeverity.Informational, nameof(vm.PurchaseStatusMessage), static s => s.PurchaseStatusMessage,
            vm.RefreshPurchasesText, vm.RefreshPurchasesCommand).Root);
        stack.Append(Bar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), static s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand, closable: true).Root);
        stack.Append(Bar(WinoInfoBarSeverity.Warning, nameof(vm.SubscriptionEndedText), static s => s.SubscriptionEndedText).Root);
        stack.Append(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentErrorMessage), static s => s.HasConsentError ? s.ConsentErrorMessage : null).Root);
        stack.Append(Bar(WinoInfoBarSeverity.Warning, nameof(vm.ConsentDataDeletionStatus),
            static s => s.IsConsentDeletionPending ? Translator.WinoAccount_IntelligenceConsentDeletionPending : null).Root);
        stack.Append(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentDataDeletionStatus),
            static s => s.IsConsentDeletionFailed ? Translator.WinoAccount_IntelligenceConsentDeletionFailed : null).Root);
        bars.Root.Add(stack);

        foreach (var group in OfferGroups()) Offer(Content(group));
        foreach (var group in SubscribedGroups()) Subscribed(Content(group));
        foreach (var group in _content) group.Root.SetVisible(false);
        Settings.Bind(vm, nameof(vm.HasIntelligenceAccess), static s => s.HasIntelligenceAccess, _ => ApplyRevealed());
#if DEBUG
        Current = this;
#endif
    }

    protected override async Task NavigateAsync(NavigationMode mode, object? parameter)
    {
        try { await ViewModel.InitializeAsync(mode, parameter!); }
        finally { RunOnUI(Reveal); }
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(Current, this)) Current = null;
#endif
        return base.DeactivateAsync();
    }

    private readonly HashSet<WinoSettingsGroup> _offerGroups = [];
    private readonly HashSet<WinoSettingsGroup> _subscribedGroups = [];

    private WinoSettingsGroup Content(WinoSettingsGroup group)
    {
        _content.Add(group);
        return group;
    }

    private void Offer(WinoSettingsGroup group) => _offerGroups.Add(group);

    private void Subscribed(WinoSettingsGroup group) => _subscribedGroups.Add(group);

    private void Reveal()
    {
        if (_revealed || IsReleased) return;
        _revealed = true;
        _loading?.Root.SetVisible(false);
        ApplyRevealed();
    }

    private void ApplyRevealed()
    {
        if (!_revealed || IsReleased) return;
        var access = ViewModel.HasIntelligenceAccess;
        foreach (var group in _content)
            group.Root.SetVisible(_offerGroups.Contains(group) ? !access : !_subscribedGroups.Contains(group) || access);
    }

    private WinoInfoBar Bar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message,
        string? action = null, System.Windows.Input.ICommand? command = null, bool closable = false)
    {
        var bar = new WinoInfoBar(severity) { IsClosable = closable };
        Settings.Own(bar);
        if (action is not null && command is not null)
        {
            bar.ActionTitle = action;
            bar.ActionInvoked += _ =>
            {
                if (command.CanExecute(null)) command.Execute(null);
            };
        }
        Settings.Bind(ViewModel, property, message, text =>
        {
            bar.Message = text ?? string.Empty;
            bar.IsOpen = !string.IsNullOrWhiteSpace(text);
        });
        return bar;
    }

#if DEBUG
    internal static WinoIntelligencePage? Current { get; private set; }

    internal void DebugExpandAll()
    {
        _status?.SetExpanded(true);
        _languages?.SetExpanded(true);
    }

    internal string Describe()
    {
        var vm = ViewModel;
        var groups = string.Join(",", _content.Select(static g => g.Root.GetVisible() ? "1" : "0"));
        return $"signedIn={vm.IsSignedIn} access={vm.HasIntelligenceAccess} consent={vm.IsConsentGranted} consentBusy={vm.IsConsentBusy} revealed={_revealed} "
            + $"perks={vm.OfferPerks.Count} usage={vm.IntelligenceUsageItems.Count} limit={vm.IsLimitReached} manage={vm.CanManageSubscription} "
            + $"switch={_consent?.GetActive()} groups={groups}";
    }
#endif
}
