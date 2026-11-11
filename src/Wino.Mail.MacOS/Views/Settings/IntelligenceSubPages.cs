using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>A Wino Intelligence sub-page on the shared WinoAccountManagementPageViewModel, with one row per mailbox.</summary>
public abstract class IntelligenceSubPageViewController(WinoAccountManagementPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoAccountManagementPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly NSStackView _mailboxRows = WinoLayout.VStack(4);
    private BindingScope? _rowScope;
    private bool _rebuildQueued;

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
        => await ViewModel.InitializeAsync(mode, parameter!);

    /// <summary>The mailbox list; rows come from <see cref="CreateMailboxRow"/> and are rebuilt once per collection batch.</summary>
    protected NSStackView MailboxList()
    {
        _mailboxRows.Alignment = NSLayoutAttribute.Leading;
        Bind.Collection(ViewModel.IntelligenceMailboxes, QueueRebuild);
#if DEBUG
        WinoIntelligenceDebug.Current = ViewModel;
#endif
        return _mailboxRows;
    }

    protected virtual NSView CreateMailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows) => new();

    protected void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _rebuildQueued = false;
            if (!Bindings.IsDisposed) Rebuild();
        });
    }

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        IntelligenceSettingsViews.Clear(_mailboxRows);
        foreach (var item in ViewModel.IntelligenceMailboxes.ToList())
        {
            var row = CreateMailboxRow(item, rows);
            row.TranslatesAutoresizingMaskIntoConstraints = false;
            _mailboxRows.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_mailboxRows.WidthAnchor).Active = true;
        }
    }

    protected WinoInfoBar Bar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message)
    {
        var bar = InfoBar(severity, null, null);
        Bind.Bind(ViewModel, property, message, text => { bar.Message = text; bar.Hidden = string.IsNullOrWhiteSpace(text); });
        return bar;
    }

    protected static bool Execute(System.Windows.Input.ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) != true) return false;
        command.Execute(parameter);
        return true;
    }
}

/// <summary>Windows IntelligencePrivacyPage: AI provider access, what each feature sends, what is stored, and the policy.</summary>
public sealed class IntelligencePrivacyPageViewController(WinoAccountManagementPageViewModel viewModel, IMailDialogService dialogs,
    IDispatcher dispatcher, IWinoLogger logger) : IntelligenceSubPageViewController(viewModel, dispatcher, logger)
{
    private WinoTextSwitch? _consent;
    private bool _applyingConsent;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Add(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentErrorMessage), s => s.HasConsentError ? s.ConsentErrorMessage : null));
        Add(Bar(WinoInfoBarSeverity.Warning, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionPending ? Translator.WinoAccount_IntelligenceConsentDeletionPending : null));
        Add(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionFailed ? Translator.WinoAccount_IntelligenceConsentDeletionFailed : null));

        var busy = IntelligenceViews.Spinner();
        _consent = new WinoTextSwitch(Translator.WinoAccount_Allowed, Translator.WinoAccount_NotAllowed, Translator.WinoIntelligence_Privacy_Title);
        Bind.Bind(vm, nameof(vm.IsConsentBusy), s => s.IsConsentBusy, on => { IntelligenceViews.SetSpinning(busy, on); _consent.IsEnabled = !on; });
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, granted => { if (!_applyingConsent) _consent.IsOn = granted; });
        _consent.Toggled += async (_, _) => await ConsentToggledAsync();
        AddGroup(null, Card(Translator.WinoIntelligence_Privacy_Title, Translator.WinoIntelligence_ProviderAccess_Description, WinoIconGlyph.Shield, Row(busy, _consent)));

        NSView? assistant = null;
        if (vm.IsVoiceControlAvailable)
        {
            var card = Card(Translator.WinoIntelligence_Voice_Title, Translator.WinoIntelligence_Sends_Assistant);
            card.LeadingView = IntelligenceSettingsViews.AppLogo(24);
            assistant = card;
        }
        AddGroup(Translator.WinoIntelligence_Sends_Header,
            assistant,
            Card(Translator.WinoIntelligence_Sends_ClassificationTitle, Translator.WinoIntelligence_Sends_Classification, WinoIconGlyph.Tag),
            Card(Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Sends_Tools, WinoIconGlyph.Translate));

        AddGroup(Translator.WinoIntelligence_Stored_Header,
            vm.IsVoiceControlAvailable ? CommandCard(Translator.VoiceBrain_Title, Translator.WinoIntelligence_Stored_Memory, WinoIconGlyph.Lightbulb, vm.OpenVoiceBrainCommand) : null,
            CommandCard(Translator.WinoIntelligence_Stored_ResultsTitle, Translator.WinoIntelligence_Stored_Results, WinoIconGlyph.Tag, vm.OpenMailClassificationCommand));

        var policy = Card(Translator.WinoIntelligence_Policy_Title, vm.ConsentAcceptedText, WinoIconGlyph.Document,
            Bind.Button(Translator.WinoIntelligence_Policy_Review, vm.OpenConsentPolicyCommand));
        Bind.Bind(vm, nameof(vm.ConsentAcceptedText), s => s.ConsentAcceptedText, text => policy.Description = string.IsNullOrWhiteSpace(text) ? null : text);
        AddGroup(Translator.WinoIntelligence_Policy_Header, policy);
#if DEBUG
        WinoIntelligenceDebug.Current = vm;
#endif
    }

    private async Task ConsentToggledAsync()
    {
        var vm = ViewModel;
        if (_consent is null || _applyingConsent || vm.IsConsentBusy || _consent.IsOn == vm.IsConsentGranted) return;
        _applyingConsent = true;
        try
        {
            if (_consent.IsOn)
            {
                if (View.Window is { } window)
                    await IntelligenceConsentPolicySheet.PresentAsync(window, vm.ConsentPolicyUri, () => vm.SetIntelligenceConsentAsync(true));
            }
            else if (await dialogs.ShowConfirmationDialogAsync(Translator.WinoAccount_IntelligenceConsentDisableMessage,
                         Translator.WinoAccount_IntelligenceConsentDisableTitle, Translator.WinoAccount_DisableConsent))
            {
                await vm.SetIntelligenceConsentAsync(false);
            }
        }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            _applyingConsent = false;
            _consent.IsOn = vm.IsConsentGranted;
        }
    }
}

/// <summary>Windows IntelligenceBriefingPage: open the briefing, then one switch per account.</summary>
public sealed class IntelligenceBriefingPageViewController(WinoAccountManagementPageViewModel viewModel, IPictureStorageService pictures,
    IDispatcher dispatcher, IWinoLogger logger) : IntelligenceSubPageViewController(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        AddGroup(null, Card(Translator.WinoIntelligence_Briefing_OpenTitle, Translator.WinoIntelligence_Briefing_OpenDescription, WinoIconGlyph.DailyBriefing,
            Bind.Button(Translator.Buttons_Open, vm.OpenDailyBriefingCommand)));
        AddGroup(Translator.WinoIntelligence_Briefing_AccountsTitle, MailboxList());
        var info = InfoBar(WinoInfoBarSeverity.Informational, null, vm.BriefingInfoText);
        info.IsClosable = false;
        Bind.Bind(vm, nameof(vm.BriefingInfoText), s => s.BriefingInfoText, text => info.Message = text);
        Add(info);
    }

    protected override NSView CreateMailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows)
    {
        var toggle = new WinoTextSwitch(Translator.WinoIntelligence_Briefing_Included, Translator.WinoIntelligence_Briefing_NotIncluded, Translator.WinoIntelligence_Briefing_ToggleName);
        rows.Bind(item, nameof(item.IsInDailyBriefing), i => i.IsInDailyBriefing, on => toggle.IsOn = on);
        rows.Bind(item, nameof(item.CanChangeDailyBriefing), i => i.CanChangeDailyBriefing, enabled => toggle.IsEnabled = enabled);
        rows.Visible(toggle, item, nameof(item.IsEnabled), i => i.IsEnabled);
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn != item.IsInDailyBriefing && !Execute(ViewModel.ToggleDailyBriefingCommand, item))
                toggle.IsOn = item.IsInDailyBriefing;
        };

        var turnOn = IntelligenceSettingsViews.Link(SettingsBinder.CreateButton(Translator.WinoIntelligence_Briefing_TurnOn), Translator.WinoIntelligence_Briefing_TurnOn);
        rows.OnActivated(turnOn, () => Execute(item.OpenMailClassificationCommand ?? ViewModel.OpenMailClassificationCommand, null));
        rows.Visible(turnOn, item, nameof(item.IsEnabled), i => i.IsClassificationOff);

        var card = Card(item.Address, item.BriefingDescription, WinoIconGlyph.None, Row(toggle, turnOn));
        card.LeadingView = IntelligenceSettingsViews.MailboxIcon(item, pictures);
        rows.Bind(item, nameof(item.BriefingDescription), i => i.BriefingDescription, text => card.Description = text);
        return card;
    }
}

/// <summary>Windows MailClassificationPage: a switch per account; the row opens that account's classification page.</summary>
public sealed class MailClassificationPageViewController(WinoAccountManagementPageViewModel viewModel, IPictureStorageService pictures,
    IDispatcher dispatcher, IWinoLogger logger) : IntelligenceSubPageViewController(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        Add(Bar(WinoInfoBarSeverity.Error, nameof(vm.IntelligenceDataError), s => s.IntelligenceDataError));
        var consent = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.WinoAccount_IntelligenceConsentNotGranted);
        consent.ActionTitle = Translator.WinoIntelligence_PrivacyData_Title;
        consent.ActionInvoked += (_, _) => Execute(vm.OpenPrivacyDataCommand, null);
        Add(Bind.Visible(consent, vm, nameof(vm.IsConsentGranted), s => !s.IsConsentGranted));
        AddGroup(null, MailboxList());
    }

    protected override NSView CreateMailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows)
    {
        var toggle = new NSSwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(toggle, Translator.WinoIntelligence_MailClassification_ToggleName);
        toggle.ToolTip = item.ToggleUnavailableTooltip;
        rows.Bind(item, nameof(item.IsEnabled), i => i.IsEnabled, on => toggle.State = on ? (nint)1 : 0);
        rows.Enabled(toggle, item, nameof(item.CanChangeEnabled), i => i.CanChangeEnabled);
        rows.OnActivated(toggle, () =>
        {
            var requested = toggle.State == 1;
            toggle.State = item.IsEnabled ? 1 : 0;
            if (requested != item.IsEnabled) Execute(ViewModel.ToggleIntelligenceMailboxCommand, item);
        });

        var card = Card(item.Address, item.IntelligenceSummary, WinoIconGlyph.None, toggle);
        card.LeadingView = IntelligenceSettingsViews.MailboxIcon(item, pictures);
        if (item.OpenCommand is { } open)
        {
            card.IsClickable = true;
            EventHandler handler = (_, _) => Execute(open, item);
            card.Activated += handler;
            Bindings.Own(new ActionDisposable(() => card.Activated -= handler));
        }
        return card;
    }
}
