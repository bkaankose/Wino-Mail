using Android.Views;
using Android.Widget;
using Google.Android.Material.CheckBox;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.Accounts;

/// <summary>
/// Account details, a sub-page of Manage accounts (iOS AccountDetailsPageViewController): tabs for General, Mail,
/// Calendar, People and To Do with the Windows card order. The title follows the account name. Wide controls (text
/// fields, swatches, button rows) sit under the card text at phone width. Android has no Jump List or Dock menu, so the
/// Mail tab leaves out that switch and the per-folder Dock menu column, as on iOS.
/// </summary>
public sealed class AccountDetailsPageFragment : SettingsPageFragment<AccountDetailsPageViewModel>, ISettingsPageTitleSource
{
    private readonly LinearLayout?[] _tabs = new LinearLayout?[5];
    private WinoSettingsGroup? _calendars;
    private LinearLayout? _folderRows;
    private BindingScope? _calendarScope;
    private BindingScope? _folderScope;
    private IPictureStorageService? _pictures;

    public string? PageTitle => ViewModel?.Account?.Name;

    protected override void BuildPage()
    {
        var vm = VM;
        _pictures = MainApplication.Services?.GetService<IPictureStorageService>();
        Bindings.Own(new ActionDisposable(() =>
        {
            _calendarScope?.Dispose();
            _folderScope?.Dispose();
        }));
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ => UpdateTitle());

        // No segmented tabs on a phone (owner, 15 Oct 2026): the Windows tabs are the page's sections, in the Windows
        // order, each shown while the account has that feature.
        for (int index = 0; index < _tabs.Length; index++) _tabs[index] = Add(Stack(0), fullWidth: true);
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ =>
        {
            bool[] shown = [true, vm.HasMailAccess, vm.HasCalendarAccess, vm.HasContactAccess, vm.HasTaskAccess];
            for (int index = 0; index < _tabs.Length; index++)
                if (_tabs[index] is { } tab) tab.Visibility = shown[index] ? ViewStates.Visible : ViewStates.Gone;
        });

        BuildGeneral();
        BuildMail();
        BuildCalendar();
        BuildPeople();
        BuildToDo();
    }

    private static readonly string[] TabTitles =
    [
        Translator.AccountDetailsPage_TabGeneral, Translator.AccountDetailsPage_TabMail, Translator.AccountDetailsPage_TabCalendar,
        Translator.AccountDetailsPage_TabPeople, Translator.AccountDetailsPage_TabToDo
    ];

    /// <summary>Adds to a tab's section; the tab's first section carries the tab name as its header.</summary>
    private void AddToTab(int tab, View view)
    {
        if (_tabs[tab] is not { } stack) return;
        if (stack.ChildCount == 0)
        {
            if (view is WinoSettingsGroup { Title: null or { Length: 0 } } first) first.Title = TabTitles[tab];
            else
            {
                // A tab that starts with a notice: its name stands above it.
                var header = WinoTokens.Style(new TextView(Ctx) { Text = TabTitles[tab] }, WinoTextStyle.SettingsSectionHeader);
                AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(header, true);
                WinoTokens.Pad(header, Resource.Dimension.wino_m3_settings_header_start, Resource.Dimension.wino_m3_settings_section_gap, 0, 0);
                AddTo(stack, header, 0);
            }
        }
        if (view is WinoSettingsGroup) AddTo(stack, view, 0);
        else
        {
            // Notices sit between sections with the header inset.
            var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_section_gap)
            };
            int inset = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_header_start);
            layout.MarginStart = inset;
            layout.MarginEnd = inset;
            stack.AddView(view, layout);
        }
    }

    private void BuildGeneral()
    {
        var vm = VM;
        // The account's stored profile picture (or provider glyph), the same icon the shell pane shows.
        var picture = new WinoAccountIconView(Ctx) { Size = WinoTokens.Dp(Ctx, Resource.Dimension.wino_size_control) };
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, account =>
        {
            picture.DisplayName = account?.Name;
            picture.Account = account is null || _pictures is null ? null : MailAccountIconInfoFactory.Create(account, _pictures);
        });
        var pictureCard = Card(Translator.AccountDetailsPage_ProfilePictureTitle, Translator.AccountDetailsPage_ProfilePictureDescription);
        pictureCard.LeadingView = picture;
        pictureCard.BottomContent = AccountSettingsParts.Wrap(Ctx,
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureChange, vm.ChangeProfilePictureCommand),
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureRemove, vm.RemoveProfilePictureCommand),
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureRefresh, vm.RefreshProfilePictureCommand));

        var name = Bind.TextField(vm, nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder);
        name.ContentDescription = Translator.AccountDetailsPage_Title;

        var capabilityApply = Bind.Button(Translator.Buttons_Apply, vm.ApplyCapabilitiesCommand);
        var capabilityNotice = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_CapabilityReauthenticationMessage);
        Bind.Visible(capabilityNotice, vm, nameof(vm.IsCapabilitySelectionChanged), s => s.IsCapabilitySelectionChanged);
        var applyRow = Stack(Resource.Dimension.wino_space_s, capabilityNotice, Row(capabilityApply));
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

        var swatches = new AccountColorSwatches(Ctx, Translator.AccountDetailsPage_ColorPicker_Title);
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
        var colorCard = StackedCard(Translator.AccountDetailsPage_ColorPicker_Title, Translator.AccountDetailsPage_ColorPicker_Description, WinoIconGlyph.Color,
            Stack(Resource.Dimension.wino_space_s, swatches.View, Row(reset)));

        var imap = StackedCard(Translator.SettingsEditAccountDetails_ImapCalDavSettings_Title, Translator.SettingsEditAccountDetails_ImapCalDavSettings_Description, WinoIconGlyph.GlobeDesktop,
            Row(Bind.Button(Translator.SettingsEditAccountDetails_ImapCalDavSettings_Action, vm.EditImapCalDavSettingsCommand)));
        var protocolLog = Card(Translator.ProtocolLog_EnableTitle, Translator.ProtocolLog_EnableDescription, WinoIconGlyph.Pulse,
            Bind.Switch(vm, nameof(vm.IsProtocolLogEnabled), s => s.IsProtocolLogEnabled, (s, v) => s.IsProtocolLogEnabled = v, Translator.ProtocolLog_EnableCheckbox));
        var protocolExport = StackedCard(Translator.ProtocolLog_ExportTitle, Translator.ProtocolLog_ExportDescription, WinoIconGlyph.ArrowDownload,
            AccountSettingsParts.Wrap(Ctx, Bind.Button(Translator.ProtocolLog_ExportAction, vm.ExportProtocolLogsCommand), Bind.Button(Translator.ProtocolLog_UploadAction, vm.UploadProtocolLogsCommand)));
        foreach (var card in new View[] { imap, protocolLog, protocolExport })
            Bind.Visible(card, vm, nameof(vm.ServerInformation), s => s.IsImapServer);

        AddToTab(0, Group(null,
            pictureCard,
            StackedCard(Translator.AccountDetailsPage_Title, Translator.AccountEditDialog_Message, WinoIconGlyph.Person, name),
            capabilities,
            colorCard,
            imap,
            protocolLog,
            protocolExport,
            ActionCard(Translator.Buttons_Save, vm.SaveChangesCommand)));

        var delete = ActionCard(Translator.SettingsDeleteAccount_Title, vm.DeleteAccountCommand, destructive: true);
        AddToTab(0, Group(null, delete));
    }

    private void BuildMail()
    {
        var vm = VM;
        // Windows leaves this InfoBar at the default IsClosable="True".
        var summary = InfoBar(WinoInfoBarSeverity.Informational, Translator.AccountDetailsPage_InitialSynchronization_Title, null);
        summary.IsClosable = true;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, _ =>
        {
            summary.Message = vm.InitialSynchronizationSummary;
            summary.Visibility = vm.IsInitialSynchronizationSummaryVisible ? ViewStates.Visible : ViewStates.Gone;
        });
        AddToTab(1, summary);

        var senderName = StackedCard(Translator.AccountSettingsDialog_AccountName, Translator.SettingsEditAccountDetails_Description, WinoIconGlyph.Person,
            Bind.TextField(vm, nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder));
        Bind.Visible(senderName, vm, nameof(vm.Account), s => s.IsSenderNameEditable);
        var addressLabel = Bind.Label(vm, nameof(vm.Address), s => s.Address, WinoTextStyle.Description, maximumLines: 0);
        var address = StackedCard(Translator.IMAPSetupDialog_MailAddress, null, WinoIconGlyph.Mail, addressLabel);

        var intelligence = CommandCard(Translator.SemanticIndex_Title, Translator.SemanticIndex_Description, WinoIconGlyph.WinoIntelligence, vm.ManageWinoIntelligenceCommand);
        Bind.Visible(intelligence, vm, nameof(vm.CanAccessWinoIntelligence), s => s.CanAccessWinoIntelligence);

        AddToTab(1, Group(null,
            senderName,
            address,
            intelligence,
            CommandCard(Translator.SettingsManageAliases_Title, Translator.SettingsManageAliases_Description, WinoIconGlyph.PersonSwap, vm.EditAliasesCommand),
            CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand)));

        _folderRows = AccountSettingsParts.VColumn(Ctx);
        var folderTable = Stack(0, FolderColumnHeader(), _folderRows);
        Bind.Collection(vm.CurrentFolders, RebuildFolderRows);

        var folderOptions = Expander(Translator.SettingsFolderOptions_Title, Translator.SettingsFolderOptions_Description, WinoIconGlyph.FolderSync, null,
            CommandCard(Translator.FolderCustomization_EntryCardTitle, Translator.FolderCustomization_EntryCardDescription, WinoIconGlyph.List, vm.CustomizeFolderListCommand),
            CommandCard(Translator.SettingsConfigureSpecialFolders_Title, Translator.SettingsConfigureSpecialFolders_Description, WinoIconGlyph.Settings, vm.SetupSpecialFoldersCommand));
        // The folder table carries its own column insets.
        folderOptions.Add(folderTable, 0, 0, 0, 0);

        // No Dock menu or Jump List on Android: the notifications expander holds the unread badge entry only.
        AddToTab(1, Group(null,
            folderOptions,
            Expander(Translator.SettingsNotifications_Title, Translator.SettingsNotifications_Description, WinoIconGlyph.Alert, null,
                CommandCard(Translator.SettingsUnreadBadges_Card_Title, Translator.SettingsUnreadBadges_Card_Description, WinoIconGlyph.AlertBadge, vm.ConfigureUnreadBadgesCommand),
                Card(Translator.SettingsHideNotificationContent_Title, Translator.SettingsHideNotificationContent_Description, WinoIconGlyph.EyeOff,
                    Bind.Switch(vm, nameof(vm.IsNotificationContentHidden), s => s.IsNotificationContentHidden, (s, v) => s.IsNotificationContentHidden = v, Translator.SettingsHideNotificationContent_Title)))));

        var focused = Card(Translator.SettingsFocusedInbox_Title, Translator.SettingsFocusedInbox_Description, WinoIconGlyph.MailInboxCheckmark,
            Bind.Switch(vm, nameof(vm.IsFocusedInboxEnabled), s => s.IsFocusedInboxEnabled, (s, v) => s.IsFocusedInboxEnabled = v, Translator.SettingsFocusedInbox_Title));
        Bind.Visible(focused, vm, nameof(vm.Account), s => s.IsFocusedInboxSupportedForAccount);
        var append = Card(Translator.SettingsAccountManagementAppendMessage_Title, Translator.SettingsAccountManagementAppendMessage_Description, WinoIconGlyph.MailArrowUp,
            Bind.Switch(vm, nameof(vm.IsAppendMessageSettinEnabled), s => s.IsAppendMessageSettinEnabled, (s, v) => s.IsAppendMessageSettinEnabled = v, Translator.SettingsAccountManagementAppendMessage_Title));
        Bind.Visible(append, vm, nameof(vm.IsAppendMessageSettingVisible), s => s.IsAppendMessageSettingVisible);

        var signature = NavigationCard(Translator.SettingsSignature_Title, Translator.SettingsSignature_Description, WinoIconGlyph.Signature,
            () => AccountSettingsParts.Execute(vm.EditSignatureCommand, null, ReportError),
            Bind.Switch(vm, nameof(vm.IsSignatureEnabled), s => s.IsSignatureEnabled, (s, v) => s.IsSignatureEnabled = v, Translator.SettingsSignature_Title));

        AddToTab(1, Group(null,
            CommandCard(Translator.MailFilters_Title, Translator.MailFilters_AccountCardDescription, WinoIconGlyph.Filter, vm.ManageMailFiltersCommand),
            signature,
            focused,
            append));
    }

    private View FolderColumnHeader()
    {
        var label = AccountSettingsParts.Label(Ctx, Translator.SettingsFolderSynchronize_Title, WinoTextStyle.Caption, WinoColors.TertiaryText(Ctx), maxLines: 1);
        label.Gravity = GravityFlags.Center;
        var row = AccountSettingsParts.HRow(Ctx);
        row.AddView(new View(Ctx), AccountSettingsParts.RowItem(Ctx, weight: 1));
        row.AddView(label, new LinearLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_accounts_folder_check_column), ViewGroup.LayoutParams.WrapContent));
        WinoTokens.Pad(row, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_3xs);
        return row;
    }

    /// <summary>The Windows folder tree: every folder with its Synchronize check.</summary>
    private void RebuildFolderRows()
    {
        if (_folderRows is null) return;
        _folderScope?.Dispose();
        _folderScope = new BindingScope();
        var rows = Bind.Child(_folderScope);
        _folderRows.RemoveAllViews();
        foreach (var folder in VM.CurrentFolders.ToList()) AddFolderRows(_folderRows, rows, folder, 0);
    }

    private void AddFolderRows(LinearLayout host, SettingsBinder rows, IMailItemFolder folder, int depth)
    {
        AccountSettingsParts.AddStretched(host, FolderRow(rows, folder, depth));
        foreach (var child in folder.ChildFolders ?? []) AddFolderRows(host, rows, child, depth + 1);
    }

    private View FolderRow(SettingsBinder rows, IMailItemFolder folder, int depth)
    {
        var row = AccountSettingsParts.HRow(Ctx);
        row.SetMinimumHeight(WinoTokens.Px(Ctx, Resource.Dimension.wino_size_touch_large));
        var inset = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_l);
        row.SetPaddingRelative(inset + Math.Min(depth, 6) * WinoTokens.Px(Ctx, Resource.Dimension.wino_accounts_folder_indent), 0, inset, 0);
        row.AddView(AccountSettingsParts.Glyph(Ctx, SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType), Resource.Dimension.wino_glyph_m));
        var name = AccountSettingsParts.Label(Ctx, folder.FolderName, WinoTextStyle.Body, maxLines: 1);
        row.AddView(name, AccountSettingsParts.RowItem(Ctx, weight: 1, marginStart: Resource.Dimension.wino_space_s));

        // Folders that cannot hold mail (IsMoveTarget false) have no checks on Windows either.
        var check = new MaterialCheckBox(Ctx)
        {
            Checked = folder.IsSynchronizationEnabled,
            ContentDescription = $"{folder.FolderName}, {Translator.SettingsFolderSynchronize_Title}",
            Visibility = folder.IsMoveTarget ? ViewStates.Visible : ViewStates.Invisible
        };
        WinoMaterialAccent.Tint(check);
        EventHandler<CompoundButton.CheckedChangeEventArgs> changed = (_, args) =>
            AccountSettingsParts.Run(VM.FolderSyncToggledAsync(folder, args.IsChecked), ReportError);
        check.CheckedChange += changed;
        rows.Scope.Own(new ActionDisposable(() => check.CheckedChange -= changed));
        var column = new FrameLayout(Ctx);
        column.AddView(check, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        row.AddView(column, new LinearLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_accounts_folder_check_column), ViewGroup.LayoutParams.WrapContent)
        {
            MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s)
        });
        return row;
    }

    private void BuildCalendar()
    {
        var vm = VM;
        AddToTab(2, Group(null,
            StackedCard(Translator.CalendarAccountSettings_PrimaryCalendar, Translator.CalendarAccountSettings_PrimaryCalendarDescription, WinoIconGlyph.Star,
                Bind.PopUp<AccountDetailsPageViewModel, AccountCalendar>(vm, s => s.AccountCalendars.ToList(), calendar => calendar.Name,
                    nameof(vm.SelectedPrimaryCalendar), s => s.SelectedPrimaryCalendar, (s, v) => s.SelectedPrimaryCalendar = v,
                    itemsCollection: vm.AccountCalendars))));
        _calendars = Group(null);
        AddToTab(2, _calendars);
        Bind.Collection(vm.AccountCalendarSettingsItems, RebuildCalendars);
    }

    private void RebuildCalendars()
    {
        if (_calendars is null) return;
        _calendarScope?.Dispose();
        _calendarScope = new BindingScope();
        var rows = Bind.Child(_calendarScope);
        _calendars.Clear();
        foreach (var item in VM.AccountCalendarSettingsItems.ToList())
        {
            var dot = new View(Ctx);
            var dotSize = WinoTokens.Px(Ctx, Resource.Dimension.wino_accounts_calendar_dot);
            dot.LayoutParameters = new FrameLayout.LayoutParams(dotSize, dotSize, GravityFlags.Center);
            void Paint(string? hex)
            {
                var drawable = new global::Android.Graphics.Drawables.GradientDrawable();
                drawable.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
                drawable.SetColor(WinoColors.FromHexString(hex) ?? WinoColors.Accent(Ctx));
                dot.Background = drawable;
            }
            Paint(item.BackgroundColorHex);
            var expander = new WinoSettingsExpander(Ctx, item.Name, item.TimeZone, WinoIconGlyph.None);
            expander.HeaderCard.LeadingView = dot;
            expander.Add(new WinoSettingsCard(Ctx, Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
                rows.Switch(item, nameof(item.IsSynchronizationEnabled), s => s.IsSynchronizationEnabled, (s, v) => s.IsSynchronizationEnabled = v, Translator.CalendarAccountSettings_SyncEnabled)));
            expander.Add(new WinoSettingsCard(Ctx, Translator.CalendarAccountSettings_DefaultShowAs, Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar)
            {
                BottomContent = rows.PopUp<AccountCalendarSettingsItemViewModel, AccountCalendarShowAsOption>(item, s => s.ShowAsOptions.ToList(), option => option.DisplayText,
                    nameof(item.SelectedShowAsOption), s => s.SelectedShowAsOption, (s, v) => s.SelectedShowAsOption = v)
            });
            var swatches = new AccountColorSwatches(Ctx, Translator.CalendarAccountSettings_AccountColor) { Colors = item.AvailableColors.Select(color => color.Hex).ToList() };
            rows.Bind(item, nameof(item.SelectedColor), s => s.SelectedColor, color =>
            {
                swatches.SelectedHex = color?.Hex ?? item.BackgroundColorHex;
                Paint(color?.Hex ?? item.BackgroundColorHex);
            });
            EventHandler<string> picked = (_, hex) =>
            {
                var match = item.AvailableColors.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
                if (match is not null) item.SetBackgroundColor(match);
            };
            swatches.SelectionChanged += picked;
            rows.Scope.Own(new ActionDisposable(() => swatches.SelectionChanged -= picked));
            expander.Add(new WinoSettingsCard(Ctx, Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color)
            {
                BottomContent = swatches.View
            });
            _calendars.Add(expander);
        }
        _calendars.Visibility = _calendars.RowCount == 0 ? ViewStates.Gone : ViewStates.Visible;
    }

    private void BuildPeople()
    {
        var vm = VM;
        var reconnect = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_ContactsReauthorizationMessage);
        Bind.Visible(reconnect, vm, nameof(vm.Account), s => s.IsContactReauthorizationRequired);
        AddToTab(3, reconnect);
        AddToTab(3, Group(null,
            StackedCard(Translator.AccountDetailsPage_PeopleSourceTitle, Translator.AccountDetailsPage_PeopleSourceDescription, WinoIconGlyph.People,
                Bind.Label(vm, nameof(vm.Account), s => s.ContactIntegrationSourceText, WinoTextStyle.Description, maximumLines: 0)),
            CommandCard(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand)));
        var buttonRow = ActionCard(Translator.AccountDetailsPage_ReconnectContacts, vm.ReauthorizeContactsCommand);
        Bind.Visible(buttonRow, vm, nameof(vm.Account), s => s.IsContactReauthorizationRequired);
        AddToTab(3, Group(null, buttonRow));
    }

    private void BuildToDo()
    {
        var vm = VM;
        var reconnect = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountDetailsPage_TasksReauthorizationMessage);
        Bind.Visible(reconnect, vm, nameof(vm.Account), s => s.IsTaskReauthorizationRequired);
        AddToTab(4, reconnect);
        AddToTab(4, Group(null,
            StackedCard(Translator.AccountDetailsPage_TasksSourceTitle, Translator.AccountDetailsPage_TasksSourceDescription, WinoIconGlyph.Checkmark,
                Bind.Label(vm, nameof(vm.Account), s => s.TaskIntegrationSourceText, WinoTextStyle.Description, maximumLines: 0))));
        var buttonRow = ActionCard(Translator.AccountDetailsPage_ReconnectTasks, vm.ReauthorizeTasksCommand);
        Bind.Visible(buttonRow, vm, nameof(vm.Account), s => s.IsTaskReauthorizationRequired);
        AddToTab(4, Group(null, buttonRow));
    }
}
