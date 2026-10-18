using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Account details, a sub-page of Manage accounts (Mac AccountDetailsPageViewController): tabs for General, Mail,
/// Calendar, People and To Do with the Windows card order. The title follows the account name. Wide controls (text
/// fields, swatches, button rows) sit under the card text at phone width. iOS has no Jump List or Dock menu, so the
/// Mail tab leaves out that switch and the per-folder Dock menu column.
/// </summary>
public sealed class AccountDetailsPageViewController(AccountDetailsPageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountDetailsPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private const double FolderCheckColumnWidth = 96;
    private readonly UIStackView[] _tabs = [TabStack(), TabStack(), TabStack(), TabStack(), TabStack()];
    private readonly WinoSettingsGroup _calendars = new();
    private readonly UIStackView _folderRows = WinoLayout.VStack(0);
    private BindingScope? _calendarScope;
    private BindingScope? _folderScope;

    public string? PageTitle => ViewModel.Account?.Name;
    public event EventHandler? PageTitleChanged;

    private static UIStackView TabStack()
    {
        var stack = WinoLayout.VStack(20);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var segments = Bind.Segmented(vm,
            [Translator.AccountDetailsPage_TabGeneral, Translator.AccountDetailsPage_TabMail, Translator.AccountDetailsPage_TabCalendar,
             Translator.AccountDetailsPage_TabPeople, Translator.AccountDetailsPage_TabToDo],
            nameof(vm.SelectedTabIndex), s => s.SelectedTabIndex, (s, v) => s.SelectedTabIndex = v);
        segments.ApportionsSegmentWidthsByContent = true;
        segments.HeightAnchor.ConstraintGreaterThanOrEqualTo(32).Active = true;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ =>
        {
            segments.SetEnabled(vm.HasMailAccess, 1);
            segments.SetEnabled(vm.HasCalendarAccess, 2);
            segments.SetEnabled(vm.HasContactAccess, 3);
            segments.SetEnabled(vm.HasTaskAccess, 4);
        });
        Add(segments);
        Page.SetCustomSpacing((nfloat)WinoStyle.Space4, segments);

        foreach (var tab in _tabs) Add(tab);
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
        => ViewModel.InitializeAsync(mode, parameter);

    private void AddTo(int tab, UIView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _tabs[tab].AddArrangedSubview(view);
    }

    /// <summary>A button row that wraps nothing but keeps every button at least 44pt tall.</summary>
    private static UIStackView ButtonRow(params UIView[] buttons)
    {
        foreach (var button in buttons) button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var row = Row(buttons);
        row.Distribution = UIStackViewDistribution.FillProportionally;
        return row;
    }

    private static UIButton Destructive(UIButton button)
    {
        if (button.Configuration is { } configuration)
        {
            configuration.BaseForegroundColor = WinoStyle.Critical;
            button.Configuration = configuration;
        }
        return button;
    }

    private void BuildGeneral()
    {
        var vm = ViewModel;
        // The account's stored profile picture (or provider glyph), the same icon the shell pane shows.
        var picture = new WinoAccountIconView(40);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, account =>
            picture.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures));
        var pictureCard = Card(Translator.AccountDetailsPage_ProfilePictureTitle, Translator.AccountDetailsPage_ProfilePictureDescription);
        pictureCard.LeadingView = picture;
        pictureCard.BottomContent = ButtonRow(
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureChange, vm.ChangeProfilePictureCommand),
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureRemove, vm.RemoveProfilePictureCommand),
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureRefresh, vm.RefreshProfilePictureCommand));

        var name = Bind.TextField(vm, nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder, 0);
        WinoAccessibility.Label(name, Translator.AccountDetailsPage_Title);

        var capabilityApply = Bind.Button(Translator.Buttons_Apply, vm.ApplyCapabilitiesCommand);
        capabilityApply.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var capabilityNotice = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_CapabilityReauthenticationMessage);
        Bind.Visible(capabilityNotice, vm, nameof(vm.IsCapabilitySelectionChanged), s => s.IsCapabilitySelectionChanged);
        var applyRow = WinoLayout.VStack(WinoStyle.Space2, capabilityNotice, capabilityApply);
        applyRow.Alignment = UIStackViewAlignment.Fill;
        var capabilities = Expander(Translator.AccountDetailsPage_CapabilityTitle, Translator.AccountDetailsPage_CapabilityDescription, WinoIconGlyph.Calendar, null,
            Card(Translator.ProviderSelection_UseForMail, null, WinoIconGlyph.Mail,
                Bind.Switch(vm, nameof(vm.IsMailCapabilitySelected), s => s.IsMailCapabilitySelected, (s, v) => s.IsMailCapabilitySelected = v, Translator.ProviderSelection_UseForMail)),
            Card(Translator.ProviderSelection_UseForCalendar, null, WinoIconGlyph.Calendar,
                Bind.Switch(vm, nameof(vm.IsCalendarCapabilitySelected), s => s.IsCalendarCapabilitySelected, (s, v) => s.IsCalendarCapabilitySelected = v, Translator.ProviderSelection_UseForCalendar)),
            Card(Translator.ProviderSelection_UseForContacts, null, WinoIconGlyph.People,
                Bind.Switch(vm, nameof(vm.IsContactsCapabilitySelected), s => s.IsContactsCapabilitySelected, (s, v) => s.IsContactsCapabilitySelected = v, Translator.ProviderSelection_UseForContacts)),
            Card(Translator.ProviderSelection_UseForTasks, null, WinoIconGlyph.Checkmark,
                Bind.Switch(vm, nameof(vm.IsTasksCapabilitySelected), s => s.IsTasksCapabilitySelected, (s, v) => s.IsTasksCapabilitySelected = v, Translator.ProviderSelection_UseForTasks)),
            applyRow);
        Bind.Visible(capabilities, vm, nameof(vm.Account), s => s.IsOAuthCapabilityEditable);

        var swatches = new WinoColorSwatchPicker();
        WinoAccessibility.Label(swatches, Translator.AccountDetailsPage_ColorPicker_Title);
        Bind.Bind(vm, nameof(vm.AvailableColors), s => s.AvailableColors, colors => swatches.Colors = colors?.Select(color => color.Hex).ToList() ?? []);
        Bind.Bind(vm, nameof(vm.SelectedColor), s => s.SelectedColor, color => swatches.SelectedHex = color?.Hex);
        EventHandler<string> picked = (_, hex) =>
        {
            var match = vm.AvailableColors?.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (match is not null) vm.SelectedColor = match;
        };
        swatches.SelectionChanged += picked;
        Bindings.Own(new ActionDisposable(() => swatches.SelectionChanged -= picked));
        var reset = Bind.Button(Translator.Buttons_Reset, vm.ResetColorCommand);
        reset.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var colorContent = WinoLayout.VStack(WinoStyle.Space2, swatches, reset);
        colorContent.Alignment = UIStackViewAlignment.Leading;
        swatches.WidthAnchor.ConstraintEqualTo(colorContent.WidthAnchor).Active = true;
        var colorCard = StackedCard(Translator.AccountDetailsPage_ColorPicker_Title, Translator.AccountDetailsPage_ColorPicker_Description, WinoIconGlyph.Color, colorContent);

        var imap = Card(Translator.SettingsEditAccountDetails_ImapCalDavSettings_Title, Translator.SettingsEditAccountDetails_ImapCalDavSettings_Description, WinoIconGlyph.GlobeDesktop,
            Bind.Button(Translator.SettingsEditAccountDetails_ImapCalDavSettings_Action, vm.EditImapCalDavSettingsCommand));
        var protocolLog = StackedCard(Translator.ProtocolLog_EnableTitle, Translator.ProtocolLog_EnableDescription, WinoIconGlyph.Pulse,
            Bind.Checkbox(vm, Translator.ProtocolLog_EnableCheckbox, nameof(vm.IsProtocolLogEnabled), s => s.IsProtocolLogEnabled, (s, v) => s.IsProtocolLogEnabled = v));
        var protocolExport = StackedCard(Translator.ProtocolLog_ExportTitle, Translator.ProtocolLog_ExportDescription, WinoIconGlyph.ArrowDownload,
            ButtonRow(Bind.Button(Translator.ProtocolLog_ExportAction, vm.ExportProtocolLogsCommand), Bind.Button(Translator.ProtocolLog_UploadAction, vm.UploadProtocolLogsCommand)));
        foreach (var card in new UIView[] { imap, protocolLog, protocolExport })
            Bind.Visible(card, vm, nameof(vm.ServerInformation), s => s.IsImapServer);

        AddTo(0, Group(null,
            pictureCard,
            StackedCard(Translator.AccountDetailsPage_Title, Translator.AccountEditDialog_Message, WinoIconGlyph.Person, name),
            capabilities,
            colorCard,
            imap,
            protocolLog,
            protocolExport,
            Card(Translator.SettingsEditAccountDetails_Title, Translator.Buttons_Save, WinoIconGlyph.Save,
                Bind.Button(Translator.Buttons_Save, vm.SaveChangesCommand, primary: true))));

        var delete = Destructive(Bind.Button(Translator.Buttons_Delete, vm.DeleteAccountCommand));
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

        var senderName = StackedCard(Translator.AccountSettingsDialog_AccountName, Translator.SettingsEditAccountDetails_Description, WinoIconGlyph.Person,
            Bind.TextField(vm, nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder, 0));
        Bind.Visible(senderName, vm, nameof(vm.Account), s => s.IsSenderNameEditable);
        var addressLabel = Bind.Label(vm, nameof(vm.Address), s => s.Address, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var address = StackedCard(Translator.IMAPSetupDialog_MailAddress, null, WinoIconGlyph.Mail, addressLabel);

        var intelligence = CommandCard(Translator.SemanticIndex_Title, Translator.SemanticIndex_Description, WinoIconGlyph.WinoIntelligence, vm.ManageWinoIntelligenceCommand);
        Bind.Visible(intelligence, vm, nameof(vm.CanAccessWinoIntelligence), s => s.CanAccessWinoIntelligence);

        AddTo(1, Group(null,
            senderName,
            address,
            intelligence,
            CommandCard(Translator.SettingsManageAliases_Title, Translator.SettingsManageAliases_Description, WinoIconGlyph.PersonSwap, vm.EditAliasesCommand),
            CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand)));

        _folderRows.Alignment = UIStackViewAlignment.Fill;
        var folderTable = WinoLayout.VStack(0, FolderColumnHeader(), _folderRows);
        folderTable.Alignment = UIStackViewAlignment.Fill;
        Bind.Collection(vm.CurrentFolders, RebuildFolderRows);

        var folderOptions = Expander(Translator.SettingsFolderOptions_Title, Translator.SettingsFolderOptions_Description, WinoIconGlyph.FolderSync, null,
            CommandCard(Translator.FolderCustomization_EntryCardTitle, Translator.FolderCustomization_EntryCardDescription, WinoIconGlyph.List, vm.CustomizeFolderListCommand),
            CommandCard(Translator.SettingsConfigureSpecialFolders_Title, Translator.SettingsConfigureSpecialFolders_Description, WinoIconGlyph.Settings, vm.SetupSpecialFoldersCommand));
        // The folder table carries its own column insets.
        folderOptions.Add(folderTable, 0, 0, 0, 0);

        // No Dock menu or Jump List on iOS: the notifications expander holds the unread badge entry only.
        AddTo(1, Group(null,
            folderOptions,
            Expander(Translator.SettingsNotifications_Title, Translator.SettingsNotifications_Description, WinoIconGlyph.Alert, null,
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

    private static UIView FolderColumnHeader()
    {
        var label = WinoStyle.Label(Translator.SettingsFolderSynchronize_Title, WinoStyle.Caption, WinoStyle.TertiaryText);
        label.TextAlignment = UITextAlignment.Center;
        label.AdjustsFontSizeToFitWidth = true;
        label.WidthAnchor.ConstraintEqualTo((nfloat)FolderCheckColumnWidth).Active = true;
        var row = WinoLayout.HStack(WinoStyle.Space2, WinoLayout.Spacer(), label);
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 10, WinoSettingsStyle.CardPadding, 2, WinoSettingsStyle.CardPadding);
        return host;
    }

    /// <summary>The Windows folder tree: every folder with its Synchronize switch (the Jump List column is Windows-only).</summary>
    private void RebuildFolderRows()
    {
        _folderScope?.Dispose();
        _folderScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_folderScope);
        foreach (var view in _folderRows.ArrangedSubviews) { _folderRows.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var folder in ViewModel.CurrentFolders.ToList()) AddFolderRows(rows, folder, 0);
    }

    private void AddFolderRows(SettingsBinder rows, IMailItemFolder folder, int depth)
    {
        _folderRows.AddArrangedSubview(FolderRow(rows, folder, depth));
        foreach (var child in folder.ChildFolders ?? []) AddFolderRows(rows, child, depth + 1);
    }

    private UIView FolderRow(SettingsBinder rows, IMailItemFolder folder, int depth)
    {
        var icon = new WinoIconView(SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), 16);
        WinoLayout.Size(icon, 16, 16);
        var name = WinoStyle.Label(folder.FolderName, WinoStyle.Body, WinoStyle.PrimaryText);
        name.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 1, 250);

        // Folders that cannot hold mail (IsMoveTarget false) have no checks on Windows either.
        var sync = FolderCheck(rows, folder, Translator.SettingsFolderSynchronize_Title, folder.IsSynchronizationEnabled,
            isOn => ViewModel.FolderSyncToggledAsync(folder, isOn));

        var row = WinoLayout.HStack(WinoStyle.Space2, icon, name, sync);
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, host, 0, WinoSettingsStyle.CardPadding + Math.Min(depth, 6) * 16, 0, WinoSettingsStyle.CardPadding);
        host.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        return host;
    }

    /// <summary>A centred native switch in a fixed-width column (18 Oct 2026: the glyph check buttons drew as squares on iOS).</summary>
    private UIView FolderCheck(SettingsBinder rows, IMailItemFolder folder, string column, bool isOn, Func<bool, Task> toggled)
    {
        var toggle = new UISwitch { On = isOn, OnTintColor = WinoStyle.Accent, TranslatesAutoresizingMaskIntoConstraints = false };
        rows.OnActivated(toggle, async () =>
        {
            try { await toggled(toggle.On); }
            catch (Exception ex) { ReportError(ex); }
        }, UIControlEvent.ValueChanged);
        toggle.Hidden = !folder.IsMoveTarget;
        WinoAccessibility.Label(toggle, $"{folder.FolderName}, {column}");
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(toggle);
        NSLayoutConstraint.ActivateConstraints(
        [
            host.WidthAnchor.ConstraintEqualTo((nfloat)FolderCheckColumnWidth),
            toggle.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            toggle.CenterYAnchor.ConstraintEqualTo(host.CenterYAnchor),
            host.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget)
        ]);
        return host;
    }

    private void BuildCalendar()
    {
        var vm = ViewModel;
        AddTo(2, Group(null,
            Card(Translator.CalendarAccountSettings_PrimaryCalendar, Translator.CalendarAccountSettings_PrimaryCalendarDescription, WinoIconGlyph.Star,
                Bind.PopUp<AccountDetailsPageViewModel, AccountCalendar>(vm, s => s.AccountCalendars.ToList(), calendar => calendar.Name,
                    nameof(vm.SelectedPrimaryCalendar), s => s.SelectedPrimaryCalendar, (s, v) => s.SelectedPrimaryCalendar = v,
                    width: 140, itemsCollection: vm.AccountCalendars))));
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
                    nameof(item.SelectedShowAsOption), s => s.SelectedShowAsOption, (s, v) => s.SelectedShowAsOption = v, width: 120)));
            var swatches = new WinoColorSwatchPicker { Colors = item.AvailableColors.Select(color => color.Hex).ToList() };
            WinoAccessibility.Label(swatches, Translator.CalendarAccountSettings_AccountColor);
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
            var colorCard = new WinoSettingsCard(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color)
            {
                BottomContent = swatches
            };
            expander.Add(colorCard);
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
            StackedCard(Translator.AccountDetailsPage_PeopleSourceTitle, Translator.AccountDetailsPage_PeopleSourceDescription, WinoIconGlyph.People,
                Bind.Label(vm, nameof(vm.Account), s => s.ContactIntegrationSourceText, WinoStyle.Description, WinoStyle.SecondaryText, 0)),
            CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand)));
        var button = Bind.Button(Translator.AccountDetailsPage_ReconnectContacts, vm.ReauthorizeContactsCommand);
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var buttonRow = Row(button, WinoLayout.Spacer());
        Bind.Visible(buttonRow, vm, nameof(vm.Account), s => s.IsContactReauthorizationRequired);
        AddTo(3, buttonRow);
    }

    private void BuildToDo()
    {
        var vm = ViewModel;
        var reconnect = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_TasksReauthorizationMessage);
        Bind.Visible(reconnect, vm, nameof(vm.Account), s => s.IsTaskReauthorizationRequired);
        AddTo(4, reconnect);
        AddTo(4, Group(null,
            StackedCard(Translator.AccountDetailsPage_TasksSourceTitle, Translator.AccountDetailsPage_TasksSourceDescription, WinoIconGlyph.Checkmark,
                Bind.Label(vm, nameof(vm.Account), s => s.TaskIntegrationSourceText, WinoStyle.Description, WinoStyle.SecondaryText, 0))));
        var button = Bind.Button(Translator.AccountDetailsPage_ReconnectTasks, vm.ReauthorizeTasksCommand);
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var buttonRow = Row(button, WinoLayout.Spacer());
        Bind.Visible(buttonRow, vm, nameof(vm.Account), s => s.IsTaskReauthorizationRequired);
        AddTo(4, buttonRow);
    }
}
