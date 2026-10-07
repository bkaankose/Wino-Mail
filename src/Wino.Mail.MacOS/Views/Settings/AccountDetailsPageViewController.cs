using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Account details, a sub-page of Manage accounts: tabs for General, Mail, Calendar, People and To Do
/// with the Windows card order. The window title follows the account name.
/// </summary>
public sealed class AccountDetailsPageViewController(AccountDetailsPageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountDetailsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly NSStackView[] _tabs = [WinoLayout.VStack(20), WinoLayout.VStack(20), WinoLayout.VStack(20), WinoLayout.VStack(20), WinoLayout.VStack(20)];
    private readonly WinoSettingsGroup _calendars = new();
    private BindingScope? _calendarScope;

    public string? PageTitle => ViewModel.Account?.Name;
    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var segments = Bind.Segmented(vm,
            [Translator.AccountDetailsPage_TabGeneral, Translator.AccountDetailsPage_TabMail, Translator.AccountDetailsPage_TabCalendar,
             Translator.AccountDetailsPage_TabPeople, Translator.AccountDetailsPage_TabToDo],
            nameof(vm.SelectedTabIndex), s => s.SelectedTabIndex, (s, v) => s.SelectedTabIndex = v);
        segments.ControlSize = NSControlSize.Regular;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ =>
        {
            segments.SetEnabled(vm.HasMailAccess, 1);
            segments.SetEnabled(vm.HasCalendarAccess, 2);
            segments.SetEnabled(vm.HasContactAccess, 3);
            segments.SetEnabled(vm.HasTaskAccess, 4);
        });
        var segmentHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        segmentHost.AddSubview(segments);
        NSLayoutConstraint.ActivateConstraints(
        [
            segments.CenterXAnchor.ConstraintEqualTo(segmentHost.CenterXAnchor),
            segments.TopAnchor.ConstraintEqualTo(segmentHost.TopAnchor),
            segments.BottomAnchor.ConstraintEqualTo(segmentHost.BottomAnchor)
        ]);
        Add(segmentHost);

        foreach (var tab in _tabs)
        {
            tab.Alignment = NSLayoutAttribute.Leading;
            Add(tab);
        }
        Bind.Bind(vm, nameof(vm.SelectedTabIndex), s => s.SelectedTabIndex, index =>
        {
            for (int i = 0; i < _tabs.Length; i++) _tabs[i].Hidden = i != index;
        });

        BuildGeneral();
        BuildMail();
        BuildCalendar();
        BuildPeople();
        BuildToDo();
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeAsync(mode, parameter!);

    private void AddTo(int tab, NSView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _tabs[tab].AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_tabs[tab].WidthAnchor).Active = true;
    }

    private void BuildGeneral()
    {
        var vm = ViewModel;
        // The account's stored profile picture (or provider glyph), the same icon the shell pane shows.
        var picture = new WinoAccountIconView(40);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, account =>
            picture.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures));
        var pictureButtons = Row(
            picture,
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureChange, vm.ChangeProfilePictureCommand),
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureRemove, vm.RemoveProfilePictureCommand),
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureRefresh, vm.RefreshProfilePictureCommand));

        var name = Bind.TextField(vm, nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder);

        var capabilityApply = Bind.Button(Translator.Buttons_Apply, vm.ApplyCapabilitiesCommand);
        var capabilityNotice = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_CapabilityReauthenticationMessage);
        Bind.Visible(capabilityNotice, vm, nameof(vm.IsCapabilitySelectionChanged), s => s.IsCapabilitySelectionChanged);
        var capabilities = Expander(Translator.AccountDetailsPage_CapabilityTitle, Translator.AccountDetailsPage_CapabilityDescription, WinoIconGlyph.Calendar, null,
            Card(Translator.ProviderSelection_UseForMail, null, WinoIconGlyph.Mail,
                Bind.Switch(vm, nameof(vm.IsMailCapabilitySelected), s => s.IsMailCapabilitySelected, (s, v) => s.IsMailCapabilitySelected = v, Translator.ProviderSelection_UseForMail)),
            Card(Translator.ProviderSelection_UseForCalendar, null, WinoIconGlyph.Calendar,
                Bind.Switch(vm, nameof(vm.IsCalendarCapabilitySelected), s => s.IsCalendarCapabilitySelected, (s, v) => s.IsCalendarCapabilitySelected = v, Translator.ProviderSelection_UseForCalendar)),
            Card(Translator.ProviderSelection_UseForContacts, null, WinoIconGlyph.People,
                Bind.Switch(vm, nameof(vm.IsContactsCapabilitySelected), s => s.IsContactsCapabilitySelected, (s, v) => s.IsContactsCapabilitySelected = v, Translator.ProviderSelection_UseForContacts)),
            Card(Translator.ProviderSelection_UseForTasks, null, WinoIconGlyph.Checkmark,
                Bind.Switch(vm, nameof(vm.IsTasksCapabilitySelected), s => s.IsTasksCapabilitySelected, (s, v) => s.IsTasksCapabilitySelected = v, Translator.ProviderSelection_UseForTasks)),
            Card(string.Empty, null, WinoIconGlyph.None, Row(capabilityNotice, capabilityApply)));
        Bind.Visible(capabilities, vm, nameof(vm.Account), s => s.IsOAuthCapabilityEditable);

        var swatches = new WinoColorSwatchPicker();
        swatches.WidthAnchor.ConstraintEqualTo(260).Active = true;
        Bind.Bind(vm, nameof(vm.AvailableColors), s => s.AvailableColors, colors => swatches.Colors = colors?.Select(color => color.Hex).ToList() ?? []);
        Bind.Bind(vm, nameof(vm.SelectedColor), s => s.SelectedColor, color => swatches.SelectedHex = color?.Hex);
        EventHandler<string> picked = (_, hex) =>
        {
            var match = vm.AvailableColors?.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (match is not null) vm.SelectedColor = match;
        };
        swatches.SelectionChanged += picked;
        Bindings.Own(new ActionDisposable(() => swatches.SelectionChanged -= picked));
        var colorCard = Card(Translator.AccountDetailsPage_ColorPicker_Title, Translator.AccountDetailsPage_ColorPicker_Description, WinoIconGlyph.Color,
            Row(swatches, Bind.Button(Translator.Buttons_Reset, vm.ResetColorCommand)));

        var imap = Card(Translator.SettingsEditAccountDetails_ImapCalDavSettings_Title, Translator.SettingsEditAccountDetails_ImapCalDavSettings_Description, WinoIconGlyph.GlobeDesktop,
            Bind.Button(Translator.SettingsEditAccountDetails_ImapCalDavSettings_Action, vm.EditImapCalDavSettingsCommand));
        var protocolLog = Card(Translator.ProtocolLog_EnableTitle, Translator.ProtocolLog_EnableDescription, WinoIconGlyph.Pulse,
            Bind.Checkbox(vm, Translator.ProtocolLog_EnableCheckbox, nameof(vm.IsProtocolLogEnabled), s => s.IsProtocolLogEnabled, (s, v) => s.IsProtocolLogEnabled = v));
        var protocolExport = Card(Translator.ProtocolLog_ExportTitle, Translator.ProtocolLog_ExportDescription, WinoIconGlyph.ArrowDownload,
            Row(Bind.Button(Translator.ProtocolLog_ExportAction, vm.ExportProtocolLogsCommand), Bind.Button(Translator.ProtocolLog_UploadAction, vm.UploadProtocolLogsCommand)));
        foreach (var card in new NSView[] { imap, protocolLog, protocolExport })
            Bind.Visible(card, vm, nameof(vm.ServerInformation), s => s.IsImapServer);

        AddTo(0, Group(null,
            Card(Translator.AccountDetailsPage_ProfilePictureTitle, Translator.AccountDetailsPage_ProfilePictureDescription, WinoIconGlyph.None, pictureButtons),
            Card(Translator.AccountDetailsPage_Title, Translator.AccountEditDialog_Message, WinoIconGlyph.Person, name),
            capabilities,
            colorCard,
            imap,
            protocolLog,
            protocolExport,
            Card(Translator.SettingsEditAccountDetails_Title, Translator.Buttons_Save, WinoIconGlyph.Save,
                Bind.Button(Translator.Buttons_Save, vm.SaveChangesCommand, primary: true))));

        var delete = Bind.Button(Translator.Buttons_Delete, vm.DeleteAccountCommand);
        delete.ContentTintColor = NSColor.SystemRed;
        AddTo(0, Group(null, Card(Translator.SettingsDeleteAccount_Title, Translator.SettingsDeleteAccount_Description, WinoIconGlyph.Delete, delete)));
    }

    private void BuildMail()
    {
        var vm = ViewModel;
        // Windows leaves this InfoBar at the default IsClosable="True".
        var summary = InfoBar(WinoInfoBarSeverity.Informational, Translator.AccountDetailsPage_InitialSynchronization_Title, null);
        summary.IsClosable = true;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ =>
        {
            summary.Message = vm.InitialSynchronizationSummary;
            summary.Hidden = !vm.IsInitialSynchronizationSummaryVisible;
        });
        AddTo(1, summary);

        var senderName = Card(Translator.AccountSettingsDialog_AccountName, Translator.SettingsEditAccountDetails_Description, WinoIconGlyph.Person,
            Bind.TextField(vm, nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder));
        Bind.Visible(senderName, vm, nameof(vm.Account), s => s.IsSenderNameEditable);
        var address = Card(Translator.IMAPSetupDialog_MailAddress, null, WinoIconGlyph.Mail,
            Bind.Label(vm, nameof(vm.Address), s => s.Address, WinoStyle.Body, WinoStyle.SecondaryText));
        address.Content!.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var intelligence = CommandCard(Translator.SemanticIndex_Title, Translator.SemanticIndex_Description, WinoIconGlyph.WinoIntelligence, vm.ManageWinoIntelligenceCommand);
        Bind.Visible(intelligence, vm, nameof(vm.CanAccessWinoIntelligence), s => s.CanAccessWinoIntelligence);

        AddTo(1, Group(null,
            senderName,
            address,
            intelligence,
            CommandCard(Translator.SettingsManageAliases_Title, Translator.SettingsManageAliases_Description, WinoIconGlyph.PersonSwap, vm.EditAliasesCommand),
            CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand)));

        AddTo(1, Group(null,
            Expander(Translator.SettingsFolderOptions_Title, Translator.SettingsFolderOptions_Description, WinoIconGlyph.FolderSync, null,
                CommandCard(Translator.FolderCustomization_EntryCardTitle, Translator.FolderCustomization_EntryCardDescription, WinoIconGlyph.List, vm.CustomizeFolderListCommand),
                CommandCard(Translator.SettingsConfigureSpecialFolders_Title, Translator.SettingsConfigureSpecialFolders_Description, WinoIconGlyph.Settings, vm.SetupSpecialFoldersCommand)),
            Expander(Translator.SettingsNotificationsAndTaskbar_Title, Translator.SettingsNotificationsAndTaskbar_Description, WinoIconGlyph.Alert, null,
                CommandCard(Translator.SettingsUnreadBadges_Card_Title, Translator.SettingsUnreadBadges_Card_Description, WinoIconGlyph.AlertBadge, vm.ConfigureUnreadBadgesCommand))));

        var focused = Card(Translator.SettingsFocusedInbox_Title, Translator.SettingsFocusedInbox_Description, WinoIconGlyph.MailInboxCheckmark,
            Bind.Switch(vm, nameof(vm.IsFocusedInboxEnabled), s => s.IsFocusedInboxEnabled, (s, v) => s.IsFocusedInboxEnabled = v, Translator.SettingsFocusedInbox_Title));
        Bind.Visible(focused, vm, nameof(vm.Account), s => s.IsFocusedInboxSupportedForAccount);
        var append = Card(Translator.SettingsAccountManagementAppendMessage_Title, Translator.SettingsAccountManagementAppendMessage_Description, WinoIconGlyph.MailArrowUp,
            Bind.Switch(vm, nameof(vm.IsAppendMessageSettinEnabled), s => s.IsAppendMessageSettinEnabled, (s, v) => s.IsAppendMessageSettinEnabled = v, Translator.SettingsAccountManagementAppendMessage_Title));
        Bind.Visible(append, vm, nameof(vm.IsAppendMessageSettingVisible), s => s.IsAppendMessageSettingVisible);

        var signature = NavigationCard(Translator.SettingsSignature_Title, Translator.SettingsSignature_Description, WinoIconGlyph.Signature,
            () => vm.EditSignatureCommand.Execute(null),
            Bind.Switch(vm, nameof(vm.IsSignatureEnabled), s => s.IsSignatureEnabled, (s, v) => s.IsSignatureEnabled = v, Translator.SettingsSignature_Title));

        AddTo(1, Group(null,
            CommandCard(Translator.MailFilters_Title, Translator.MailFilters_AccountCardDescription, WinoIconGlyph.Filter, vm.ManageMailFiltersCommand),
            signature,
            focused,
            append));
    }

    private void BuildCalendar()
    {
        var vm = ViewModel;
        AddTo(2, Group(null,
            Card(Translator.CalendarAccountSettings_PrimaryCalendar, Translator.CalendarAccountSettings_PrimaryCalendarDescription, WinoIconGlyph.Star,
                Bind.PopUp<AccountDetailsPageViewModel, AccountCalendar>(vm, s => s.AccountCalendars.ToList(), calendar => calendar.Name,
                    nameof(vm.SelectedPrimaryCalendar), s => s.SelectedPrimaryCalendar, (s, v) => s.SelectedPrimaryCalendar = v,
                    width: 180, itemsCollection: vm.AccountCalendars))));
        AddTo(2, _calendars);
        Bind.Collection(vm.AccountCalendarSettingsItems, RebuildCalendars);
    }

    private void RebuildCalendars()
    {
        _calendarScope?.Dispose();
        _calendarScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_calendarScope);
        _calendars.Clear();
        foreach (var item in ViewModel.AccountCalendarSettingsItems.ToList())
        {
            var dot = new WinoSurfaceView { CornerRadius = 5, Fill = WinoStyle.FromHexString(item.BackgroundColorHex) ?? WinoStyle.Accent };
            WinoLayout.Size(dot, 10, 10);
            var expander = new WinoSettingsExpander(item.Name, item.TimeZone, WinoIconGlyph.None);
            expander.HeaderCard.LeadingView = dot;
            expander.Add(new WinoSettingsCard(Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
                rows.Switch(item, nameof(item.IsSynchronizationEnabled), s => s.IsSynchronizationEnabled, (s, v) => s.IsSynchronizationEnabled = v, Translator.CalendarAccountSettings_SyncEnabled)));
            expander.Add(new WinoSettingsCard(Translator.CalendarAccountSettings_DefaultShowAs, Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar,
                rows.PopUp<AccountCalendarSettingsItemViewModel, AccountCalendarShowAsOption>(item, s => s.ShowAsOptions.ToList(), option => option.DisplayText,
                    nameof(item.SelectedShowAsOption), s => s.SelectedShowAsOption, (s, v) => s.SelectedShowAsOption = v, width: 150)));
            var swatches = new WinoColorSwatchPicker { Colors = item.AvailableColors.Select(color => color.Hex).ToList() };
            swatches.WidthAnchor.ConstraintEqualTo(260).Active = true;
            rows.Bind(item, nameof(item.SelectedColor), s => s.SelectedColor, color =>
            {
                swatches.SelectedHex = color?.Hex ?? item.BackgroundColorHex;
                dot.Fill = WinoStyle.FromHexString(color?.Hex ?? item.BackgroundColorHex) ?? WinoStyle.Accent;
            });
            EventHandler<string> picked = (_, hex) =>
            {
                var match = item.AvailableColors.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
                if (match is not null) item.SetBackgroundColor(match);
            };
            swatches.SelectionChanged += picked;
            rows.Scope.Own(new SettingsDisposable(() => swatches.SelectionChanged -= picked));
            expander.Add(new WinoSettingsCard(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color, swatches));
            _calendars.Add(expander);
        }
        _calendars.Hidden = _calendars.RowCount == 0;
    }

    private void BuildPeople()
    {
        var vm = ViewModel;
        var reconnect = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_ContactsReauthorizationMessage);
        Bind.Visible(reconnect, vm, nameof(vm.Account), s => s.IsContactReauthorizationRequired);
        AddTo(3, reconnect);
        AddTo(3, Group(null,
            Card(Translator.AccountDetailsPage_PeopleSourceTitle, Translator.AccountDetailsPage_PeopleSourceDescription, WinoIconGlyph.People,
                Bind.Label(vm, nameof(vm.Account), s => s.ContactIntegrationSourceText, WinoStyle.Description, WinoStyle.SecondaryText)),
            CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand)));
        var button = Bind.Button(Translator.AccountDetailsPage_ReconnectContacts, vm.ReauthorizeContactsCommand);
        Bind.Visible(button, vm, nameof(vm.Account), s => s.IsContactReauthorizationRequired);
        AddTo(3, button);
    }

    private void BuildToDo()
    {
        var vm = ViewModel;
        var reconnect = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_TasksReauthorizationMessage);
        Bind.Visible(reconnect, vm, nameof(vm.Account), s => s.IsTaskReauthorizationRequired);
        AddTo(4, reconnect);
        AddTo(4, Group(null,
            Card(Translator.AccountDetailsPage_TasksSourceTitle, Translator.AccountDetailsPage_TasksSourceDescription, WinoIconGlyph.Checkmark,
                Bind.Label(vm, nameof(vm.Account), s => s.TaskIntegrationSourceText, WinoStyle.Description, WinoStyle.SecondaryText))));
        var button = Bind.Button(Translator.AccountDetailsPage_ReconnectTasks, vm.ReauthorizeTasksCommand);
        Bind.Visible(button, vm, nameof(vm.Account), s => s.IsTaskReauthorizationRequired);
        AddTo(4, button);
    }
}
