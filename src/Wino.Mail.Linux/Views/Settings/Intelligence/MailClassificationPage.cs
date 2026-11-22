using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>
/// Smart labels and the daily briefing per mailbox (Windows MailClassificationPage, macOS
/// MailClassificationPageViewController): the data error, the consent warning with a way to the Intelligence page, the
/// briefing entry and one row per mailbox with its switch; a row of a mailbox on this device opens its management page.
/// </summary>
internal sealed class MailClassificationPage(WinoAccountManagementPageViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
    : SettingsPageView<WinoAccountManagementPageViewModel>(viewModel, services)
{
    private WinoSettingsGroup? _mailboxes;
    private SettingsBinder? _rows;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var bars = Page.AddGroup();
        bars.HidesWhenEmpty = false;
        var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        var error = new WinoInfoBar(WinoInfoBarSeverity.Error);
        Settings.Own(error);
        Settings.Bind(vm, nameof(vm.IntelligenceDataError), static s => s.IntelligenceDataError, text =>
        {
            error.Message = text ?? string.Empty;
            error.IsOpen = !string.IsNullOrWhiteSpace(text);
        });
        var consent = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.WinoAccount_IntelligenceConsentNotGranted)
        {
            ActionTitle = Translator.WinoIntelligence_Privacy_Title
        };
        Settings.Own(consent);
        consent.ActionInvoked += _ => Execute(vm.OpenIntelligencePageCommand, null);
        Settings.Bind(vm, nameof(vm.IsConsentGranted), static s => s.IsConsentGranted, granted => consent.IsOpen = !granted);
        stack.Append(error.Root);
        stack.Append(consent.Root);
        bars.Root.Add(stack);

        var open = WinoA11y.Rename(Settings.Button(Translator.Buttons_Open, vm.OpenDailyBriefingCommand), $"{Translator.Buttons_Open} {Translator.WinoIntelligence_Briefing_OpenTitle}");
        open.SetValign(Gtk.Align.Center);
        var briefing = WinoSettingsRows.Action(Translator.WinoIntelligence_Briefing_OpenTitle, Translator.WinoIntelligence_Briefing_OpenDescription, WinoIconGlyph.DailyBriefing);
        WinoSettingsRows.Suffix(briefing, open);
        AddGroup(null, briefing);

        _mailboxes = Page.AddGroup(Translator.WinoIntelligence_MailAccountsHeader);
        Settings.Collection(vm.IntelligenceMailboxes, Rebuild);
#if DEBUG
        Current = this;
#endif
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter!);

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(Current, this)) Current = null;
#endif
        return base.DeactivateAsync();
    }

    private void Rebuild()
    {
        if (_mailboxes is not { } group) return;
        _rows?.Scope.Dispose();
        var rows = _rows = Settings.Child();
        group.Clear();
        foreach (var item in ViewModel.IntelligenceMailboxes.ToList()) group.Add(MailboxRow(item, rows));
    }

    private Adw.ActionRow MailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows)
    {
        var row = WinoSettingsRows.Action(item.Address, item.SmartLabelsDescription);
        var icon = new WinoAccountIcon(28)
        {
            Account = item.Account is not null
                ? MailAccountIconInfoFactory.Create(item.Account, pictures)
                : MailAccountIconInfoFactory.CreateProviderFallback(item.ProviderType, item.SpecialProvider),
            AccessibleName = item.Address,
        };
        row.AddPrefix(icon.Root);
        rows.Subtitle(row, item, nameof(item.SmartLabelsDescription), static i => i.SmartLabelsDescription);

        var toggle = Gtk.Switch.New();
        toggle.SetValign(Gtk.Align.Center);
        WinoA11y.Label(toggle, $"{Translator.WinoIntelligence_MailClassification_ToggleName} {item.Address}");
        toggle.SetTooltipText(item.ToggleUnavailableTooltip);
        var applying = false;
        rows.Bind(item, nameof(item.IsEnabled), static i => i.IsEnabled, on =>
        {
            applying = true;
            try { toggle.SetActive(on); }
            finally { applying = false; }
        });
        rows.Enabled(toggle, item, nameof(item.CanChangeEnabled), static i => i.CanChangeEnabled);
        rows.Own(GtkSignals.OnNotify(toggle, "active", () =>
        {
            if (applying) return;
            var requested = toggle.GetActive();
            applying = true;
            try { toggle.SetActive(item.IsEnabled); }
            finally { applying = false; }
            if (requested != item.IsEnabled) Execute(ViewModel.ToggleIntelligenceMailboxCommand, item);
        }));
        WinoSettingsRows.Suffix(row, toggle);

        if (item.OpenCommand is { } openCommand)
        {
            var open = WinoSettingsRows.Suffix(row, WinoSettingsRows.ChevronButton($"{Translator.Buttons_Open} {item.Address}"));
            row.SetActivatableWidget(open);
            rows.Own(GtkSignals.OnClicked(open, () => Execute(openCommand, item)));
        }
        return row;
    }

    private static bool Execute(System.Windows.Input.ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) != true) return false;
        command.Execute(parameter);
        return true;
    }

#if DEBUG
    internal static MailClassificationPage? Current { get; private set; }

    internal string Describe()
        => $"consent={ViewModel.IsConsentGranted} mailboxes={ViewModel.IntelligenceMailboxes.Count} rows={_mailboxes?.Count ?? 0} "
           + string.Join(" ", ViewModel.IntelligenceMailboxes.Select(static m => $"[{m.Address} enabled={m.IsEnabled} canToggle={m.CanChangeEnabled}]"));
#endif
}
