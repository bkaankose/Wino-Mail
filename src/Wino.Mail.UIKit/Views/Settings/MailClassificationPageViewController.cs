using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Windows MailClassificationPage (Smart Labels and Daily Briefing) on the shared WinoAccountManagementPageViewModel:
/// the AI provider access reminder, Open daily briefing, then one indexing switch per mail account; a row whose account
/// is on this device opens that account's Wino Intelligence page.
/// </summary>
public sealed class MailClassificationPageViewController(WinoAccountManagementPageViewModel viewModel, IPictureStorageService pictures,
    IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoAccountManagementPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _accounts = new(Translator.WinoIntelligence_MailAccountsHeader);
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var dataError = InfoBar(WinoInfoBarSeverity.Error, null, null);
        Bind.Bind(vm, nameof(vm.IntelligenceDataError), s => s.IntelligenceDataError, text => { dataError.Message = text; dataError.Hidden = string.IsNullOrWhiteSpace(text); });
        Add(dataError);

        var consent = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.WinoAccount_IntelligenceConsentNotGranted);
        consent.AccessibilityIdentifier = "SmartLabelsConsentRequiredInfoBar";
        consent.ActionTitle = Translator.WinoIntelligence_Privacy_Title;
        EventHandler openPrivacy = (_, _) => { if (vm.OpenIntelligencePageCommand.CanExecute(null)) vm.OpenIntelligencePageCommand.Execute(null); };
        consent.ActionInvoked += openPrivacy;
        Bindings.Own(new ActionDisposable(() => consent.ActionInvoked -= openPrivacy));
        Add(Bind.Visible(consent, vm, nameof(vm.IsConsentGranted), s => !s.IsConsentGranted));

        var briefing = Bind.Button(Translator.WinoIntelligence_Briefing_OpenTitle, vm.OpenDailyBriefingCommand);
        briefing.AccessibilityIdentifier = "SmartLabelsOpenBriefingButton";
        AddGroup(null, ActionRow(briefing));

        _accounts.AccessibilityIdentifier = "SmartLabelsAccountList";
        Add(_accounts);
        Bind.Collection(vm.IntelligenceMailboxes, QueueRebuild);
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
        => await ViewModel.InitializeAsync(mode, parameter);

    /// <summary>A snapshot apply clears and refills the collection; the rows are rebuilt once per batch.</summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            if (!Bindings.IsDisposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var scope = _rowScope;
        var rows = Bind.Child(scope);
        _accounts.Clear();
        foreach (var item in ViewModel.IntelligenceMailboxes.ToList()) _accounts.Add(MailboxRow(item, rows, scope));
        _accounts.Hidden = _accounts.RowCount == 0;
    }

    /// <summary>Windows SmartLabelsMailboxTemplate: the account picture, address and state, and the indexing switch.</summary>
    private WinoSettingsCard MailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows, BindingScope scope)
    {
        var toggle = new UISwitch { TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityIdentifier = item.ToggleAutomationId };
        WinoAccessibility.Label(toggle, Translator.WinoIntelligence_MailClassification_ToggleName);
        WinoAccessibility.Help(toggle, item.ToggleUnavailableTooltip);
        rows.Bind(item, nameof(item.IsEnabled), i => i.IsEnabled, on => toggle.On = on);
        rows.Enabled(toggle, item, nameof(item.CanChangeEnabled), i => i.CanChangeEnabled);
        rows.OnActivated(toggle, () => _ = ToggleAsync(item, toggle), UIControlEvent.ValueChanged);

        var card = Card(item.Address, item.SmartLabelsDescription, WinoIconGlyph.None, toggle);
        card.AccessibilityIdentifier = item.ManageAutomationId;
        card.LeadingView = new WinoAccountIconView(28)
        {
            Account = item.Account is not null
                ? MailAccountIconInfoFactory.Create(item.Account, pictures)
                : MailAccountIconInfoFactory.CreateProviderFallback(item.ProviderType, item.SpecialProvider)
        };
        rows.Bind(item, nameof(item.SmartLabelsDescription), i => i.SmartLabelsDescription, text => card.Description = text);
        if (item.CanManage && item.OpenCommand is { } open)
        {
            card.IsClickable = true;
            EventHandler handler = (_, _) => { if (open.CanExecute(item)) open.Execute(item); };
            card.Activated += handler;
            scope.Own(new ActionDisposable(() => card.Activated -= handler));
        }
        return card;
    }

    private async Task ToggleAsync(WinoIntelligenceMailboxItemViewModel item, UISwitch toggle)
    {
        if (toggle.On == item.IsEnabled) return;
        var command = ViewModel.ToggleIntelligenceMailboxCommand;
        try
        {
            if (command.CanExecute(item)) await command.ExecuteAsync(item);
        }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            // The command leaves the state unchanged when it refuses (no access, a change in flight); show the real state.
            if (!Bindings.IsDisposed && toggle.Handle != IntPtr.Zero) toggle.On = item.IsEnabled;
        }
    }
}
