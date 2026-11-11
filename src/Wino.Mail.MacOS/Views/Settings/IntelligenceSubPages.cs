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

/// <summary>Windows MailClassificationPage (Smart Labels and Daily Briefing): open the briefing, then an indexing switch per account; the row opens that account's page.</summary>
public sealed class MailClassificationPageViewController(WinoAccountManagementPageViewModel viewModel, IPictureStorageService pictures,
    IDispatcher dispatcher, IWinoLogger logger) : IntelligenceSubPageViewController(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        Add(Bar(WinoInfoBarSeverity.Error, nameof(vm.IntelligenceDataError), s => s.IntelligenceDataError));
        var consent = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.WinoAccount_IntelligenceConsentNotGranted);
        consent.ActionTitle = Translator.WinoIntelligence_Privacy_Title;
        consent.ActionInvoked += (_, _) => Execute(vm.OpenIntelligencePageCommand, null);
        Add(Bind.Visible(consent, vm, nameof(vm.IsConsentGranted), s => !s.IsConsentGranted));
        AddGroup(null, Card(Translator.WinoIntelligence_Briefing_OpenTitle, Translator.WinoIntelligence_Briefing_OpenDescription, WinoIconGlyph.DailyBriefing,
            Bind.Button(Translator.Buttons_Open, vm.OpenDailyBriefingCommand)));
        AddGroup(Translator.WinoIntelligence_MailAccountsHeader, MailboxList());
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

        var card = Card(item.Address, item.SmartLabelsDescription, WinoIconGlyph.None, toggle);
        card.LeadingView = IntelligenceSettingsViews.MailboxIcon(item, pictures);
        rows.Bind(item, nameof(item.SmartLabelsDescription), i => i.SmartLabelsDescription, text => card.Description = text);
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
