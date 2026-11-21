using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Settings;

// The Mail, Calendar, People and To Do tabs.
internal sealed partial class AccountDetailsPage
{
    private readonly List<Gtk.Widget> _folderWidgets = [];
    private readonly List<WinoColorSwatches> _calendarSwatches = [];
    private Adw.ExpanderRow? _folderOptions;
    private WinoSettingsGroup? _calendarGroup;
    private SettingsBinder? _folderRows;
    private SettingsBinder? _calendarRows;

    private void BuildMail(WinoSettingsPage page)
    {
        var vm = ViewModel;
        var summary = page.AddBanner(Translator.AccountDetailsPage_InitialSynchronization_Title);
        _settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ =>
        {
            summary.SetTitle($"{Translator.AccountDetailsPage_InitialSynchronization_Title}: {vm.InitialSynchronizationSummary}");
            summary.SetRevealed(vm.IsInitialSynchronizationSummaryVisible);
        });

        var senderName = _settings.Text(vm, Translator.AccountSettingsDialog_AccountName, nameof(vm.SenderName), static s => s.SenderName, static (s, v) => s.SenderName = v);
        WinoSettingsRows.SetGlyph(senderName, WinoIconGlyph.Person);
        BindVisible(senderName, nameof(vm.IsSenderNameEditable), static s => s.IsSenderNameEditable);
        var senderSave = WinoSettingsRows.Action(Translator.SettingsEditAccountDetails_Title, Translator.SettingsEditAccountDetails_Description, WinoIconGlyph.Save);
        WinoSettingsRows.Suffix(senderSave, Named(Translator.Buttons_Save, vm.SaveChangesCommand));
        BindVisible(senderSave, nameof(vm.IsSenderNameEditable), static s => s.IsSenderNameEditable);

        var address = WinoSettingsRows.Action(Translator.IMAPSetupDialog_MailAddress, null, WinoIconGlyph.Mail);
        _settings.Subtitle(address, vm, nameof(vm.Address), static s => s.Address);
        var intelligence = _settings.Visible(_settings.Command(Translator.SemanticIndex_Title, Translator.SemanticIndex_Description, WinoIconGlyph.WinoIntelligence, vm.ManageWinoIntelligenceCommand),
            vm, nameof(vm.CanAccessWinoIntelligence), static s => s.CanAccessWinoIntelligence);
        page.AddGroup(null, null, senderName, senderSave, address, intelligence,
            _settings.Command(Translator.SettingsManageAliases_Title, Translator.SettingsManageAliases_Description, WinoIconGlyph.PersonSwap, vm.EditAliasesCommand),
            _settings.Command(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand));

        _folderOptions = WinoSettingsRows.Expander(Translator.SettingsFolderOptions_Title, Translator.SettingsFolderOptions_Description, WinoIconGlyph.FolderSync, false,
            _settings.Command(Translator.FolderCustomization_EntryCardTitle, Translator.FolderCustomization_EntryCardDescription, WinoIconGlyph.List, vm.CustomizeFolderListCommand),
            _settings.Command(Translator.SettingsConfigureSpecialFolders_Title, Translator.SettingsConfigureSpecialFolders_Description, WinoIconGlyph.Settings, vm.SetupSpecialFoldersCommand));
        // The Windows taskbar and macOS Dock menu rows have no Linux counterpart.
        var notifications = WinoSettingsRows.Expander(Translator.SettingsNotifications_Title, Translator.SettingsNotifications_Description, WinoIconGlyph.Alert, false,
            _settings.Command(Translator.SettingsUnreadBadges_Card_Title, Translator.SettingsUnreadBadges_Card_Description, WinoIconGlyph.AlertBadge, vm.ConfigureUnreadBadgesCommand),
            _settings.Switch(vm, Translator.SettingsHideNotificationContent_Title, Translator.SettingsHideNotificationContent_Description, WinoIconGlyph.EyeOff,
                nameof(vm.IsNotificationContentHidden), static s => s.IsNotificationContentHidden, static (s, v) => s.IsNotificationContentHidden = v));
        page.AddGroup(null, null, _folderOptions, notifications);
        _settings.Collection(vm.CurrentFolders, RebuildFolders);

        var signature = WinoSettingsRows.Action(Translator.SettingsSignature_Title, Translator.SettingsSignature_Description, WinoIconGlyph.Signature);
        WinoSettingsRows.Suffix(signature, _settings.SwitchSuffix(vm, Translator.SettingsSignature_Title,
            nameof(vm.IsSignatureEnabled), static s => s.IsSignatureEnabled, static (s, v) => s.IsSignatureEnabled = v));
        var editSignature = WinoSettingsRows.Suffix(signature, WinoSettingsRows.ChevronButton(Translator.SettingsSignature_Title));
        _settings.Own(GtkSignals.OnClicked(editSignature, () => vm.EditSignatureCommand.Execute(null)));
        var focused = _settings.Switch(vm, Translator.SettingsFocusedInbox_Title, Translator.SettingsFocusedInbox_Description, WinoIconGlyph.MailInboxCheckmark,
            nameof(vm.IsFocusedInboxEnabled), static s => s.IsFocusedInboxEnabled, static (s, v) => s.IsFocusedInboxEnabled = v);
        BindVisible(focused, nameof(vm.IsFocusedInboxSupportedForAccount), static s => s.IsFocusedInboxSupportedForAccount);
        var append = _settings.Visible(_settings.Switch(vm, Translator.SettingsAccountManagementAppendMessage_Title, Translator.SettingsAccountManagementAppendMessage_Description,
            WinoIconGlyph.MailArrowUp, nameof(vm.IsAppendMessageSettinEnabled), static s => s.IsAppendMessageSettinEnabled, static (s, v) => s.IsAppendMessageSettinEnabled = v),
            vm, nameof(vm.IsAppendMessageSettingVisible), static s => s.IsAppendMessageSettingVisible);
        page.AddGroup(null, null,
            _settings.Command(Translator.MailFilters_Title, Translator.MailFilters_AccountCardDescription, WinoIconGlyph.Filter, vm.ManageMailFiltersCommand),
            signature, focused, append);
    }

    /// <summary>Every folder with its Synchronize switch, indented by depth; a folder that cannot hold mail has none (as on Windows).</summary>
    private void RebuildFolders()
    {
        if (_folderOptions is null || IsReleased) return;
        _folderRows?.Scope.Dispose();
        var rows = _folderRows = _settings.Child();
        foreach (var widget in _folderWidgets) _folderOptions.Remove(widget);
        _folderWidgets.Clear();

        void Add(IMailItemFolder folder, int depth)
        {
            var name = folder.ResolvedDisplayName ?? string.Empty;
            Adw.ActionRow row;
            if (folder.IsMoveTarget)
            {
                var toggle = Adw.SwitchRow.New();
                toggle.SetUseMarkup(false);
                toggle.SetTitle(name);
                toggle.SetSubtitle(Translator.SettingsFolderSynchronize_Title);
                toggle.SetActive(folder.IsSynchronizationEnabled);
                rows.Own(GtkSignals.OnNotify(toggle, "active", () =>
                {
                    var enabled = toggle.GetActive();
                    if (enabled == folder.IsSynchronizationEnabled) return;
                    folder.IsSynchronizationEnabled = enabled;
                    Observe(ToggleFolderAsync(folder, enabled, toggle));
                }));
                row = toggle;
            }
            else
            {
                row = WinoSettingsRows.Action(name);
            }
            var glyph = SpecialFolderGlyphs.GetGlyph(folder.SpecialFolderType);
            WinoSettingsRows.SetGlyph(row, glyph == WinoIconGlyph.None ? WinoIconGlyph.Folder : glyph);
            row.SetMarginStart(depth * 16);
            _folderOptions.AddRow(row);
            _folderWidgets.Add(row);
            foreach (var child in folder.ChildFolders ?? []) Add(child, depth + 1);
        }

        foreach (var folder in ViewModel.CurrentFolders.ToList()) Add(folder, 0);
    }

    private async Task ToggleFolderAsync(IMailItemFolder folder, bool enabled, Adw.SwitchRow toggle)
    {
        try { await ViewModel.FolderSyncToggledAsync(folder, enabled); }
        catch
        {
            folder.IsSynchronizationEnabled = !enabled;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!IsReleased) toggle.SetActive(!enabled);
            });
            throw;
        }
    }

    private void BuildCalendar(WinoSettingsPage page)
    {
        var vm = ViewModel;
        page.AddGroup(null, null,
            _settings.Choice<AccountDetailsPageViewModel, AccountCalendar>(vm, Translator.CalendarAccountSettings_PrimaryCalendar,
                Translator.CalendarAccountSettings_PrimaryCalendarDescription, WinoIconGlyph.Star,
                static s => s.AccountCalendars, static calendar => calendar.Name ?? string.Empty,
                nameof(vm.SelectedPrimaryCalendar), static s => s.SelectedPrimaryCalendar, static (s, v) => s.SelectedPrimaryCalendar = v,
                itemsCollection: vm.AccountCalendars));
        _calendarGroup = page.AddGroup();
        _settings.Collection(vm.AccountCalendarSettingsItems, RebuildCalendars);
    }

    private void RebuildCalendars()
    {
        if (_calendarGroup is null || IsReleased) return;
        _calendarRows?.Scope.Dispose();
        var rows = _calendarRows = _settings.Child();
        foreach (var swatches in _calendarSwatches) swatches.Release();
        _calendarSwatches.Clear();
        _calendarGroup.Clear();
        var vm = ViewModel;

        foreach (var item in vm.AccountCalendarSettingsItems.ToList())
        {
            var expander = WinoSettingsRows.Expander(item.Name ?? string.Empty, item.TimeZone);
            var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            dot.AddCssClass("wino-color-dot");
            dot.SetValign(Gtk.Align.Center);
            string? dotClass = null;
            expander.AddPrefix(dot);

            if (item.IsSynchronizationConfigurable)
            {
                expander.AddRow(rows.Switch(item, Translator.CalendarAccountSettings_SyncEnabled, Translator.CalendarAccountSettings_SyncEnabledDescription, WinoIconGlyph.Sync,
                    nameof(item.IsSynchronizationEnabled), static s => s.IsSynchronizationEnabled, (s, value) =>
                    {
                        if (s.IsSynchronizationEnabled == value) return;
                        s.IsSynchronizationEnabled = value;
                        Observe(vm.UpdateCalendarSynchronizationAsync(s.Calendar, value));
                    }));
            }
            expander.AddRow(rows.Choice<AccountCalendarSettingsItemViewModel, AccountCalendarShowAsOption>(item, Translator.CalendarAccountSettings_DefaultShowAs,
                Translator.CalendarAccountSettings_DefaultShowAsDescription, WinoIconGlyph.Calendar,
                static s => s.ShowAsOptions, static option => option.DisplayText ?? string.Empty,
                nameof(item.SelectedShowAsOption), static s => s.SelectedShowAsOption, (s, value) =>
                {
                    if (ReferenceEquals(s.SelectedShowAsOption, value)) return;
                    s.SelectedShowAsOption = value;
                    Observe(vm.UpdateCalendarDefaultShowAsAsync(s.Calendar, value));
                }));

            var swatches = new WinoColorSwatches(Translator.CalendarAccountSettings_AccountColor)
            {
                Colors = item.AvailableColors.Select(static color => color.Hex).ToList()
            };
            _calendarSwatches.Add(swatches);
            rows.Bind(item, nameof(item.SelectedColor), static s => s.SelectedColor, color =>
            {
                var hex = color?.Hex ?? item.BackgroundColorHex;
                swatches.SelectedHex = hex;
                var next = WinoTint.FillClassFor(hex, null);
                if (dotClass is not null) dot.RemoveCssClass(dotClass);
                if (next is not null) dot.AddCssClass(next);
                dotClass = next;
            });
            swatches.SelectionChanged += hex =>
            {
                var match = item.AvailableColors.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
                if (match is not null) Observe(vm.UpdateCalendarColorAsync(item, match));
            };
            var colorRow = WinoSettingsRows.Action(Translator.CalendarAccountSettings_AccountColor, Translator.CalendarAccountSettings_AccountColorDescription, WinoIconGlyph.Color);
            expander.AddRow(colorRow);
            expander.AddRow(WinoSettingsRows.Custom(swatches.Root, Translator.CalendarAccountSettings_AccountColor));
            WinoA11y.Rename(expander, $"{item.Name}, {item.TimeZone}");
            _calendarGroup.Add(expander);
        }
    }

    private void BuildPeople(WinoSettingsPage page)
    {
        var vm = ViewModel;
        var banner = page.AddBanner(Translator.AccountDetailsPage_ContactsReauthorizationMessage);
        _settings.Bind(vm, nameof(vm.Account), static s => s.IsContactReauthorizationRequired, banner.SetRevealed);
        var source = WinoSettingsRows.Action(Translator.AccountDetailsPage_PeopleSourceTitle, Translator.AccountDetailsPage_PeopleSourceDescription, WinoIconGlyph.People);
        WinoSettingsRows.Suffix(source, _settings.LabelSuffix(vm, nameof(vm.Account), static s => s.ContactIntegrationSourceText));
        var reconnect = WinoSettingsRows.Action(Translator.AccountDetailsPage_ReconnectContacts);
        WinoSettingsRows.Suffix(reconnect, Named(Translator.AccountDetailsPage_ReconnectContacts, vm.ReauthorizeContactsCommand, suggested: true));
        BindVisible(reconnect, nameof(vm.IsContactReauthorizationRequired), static s => s.IsContactReauthorizationRequired);
        page.AddGroup(null, null, source,
            _settings.Command(Translator.SettingsMailCategories_Title, Translator.SettingsMailCategories_Description, WinoIconGlyph.Tag, vm.EditCategoriesCommand),
            reconnect);
    }

    private void BuildToDo(WinoSettingsPage page)
    {
        var vm = ViewModel;
        var banner = page.AddBanner(Translator.AccountDetailsPage_TasksReauthorizationMessage);
        _settings.Bind(vm, nameof(vm.Account), static s => s.IsTaskReauthorizationRequired, banner.SetRevealed);
        var source = WinoSettingsRows.Action(Translator.AccountDetailsPage_TasksSourceTitle, Translator.AccountDetailsPage_TasksSourceDescription, WinoIconGlyph.Checkmark);
        WinoSettingsRows.Suffix(source, _settings.LabelSuffix(vm, nameof(vm.Account), static s => s.TaskIntegrationSourceText));
        var reconnect = WinoSettingsRows.Action(Translator.AccountDetailsPage_ReconnectTasks);
        WinoSettingsRows.Suffix(reconnect, Named(Translator.AccountDetailsPage_ReconnectTasks, vm.ReauthorizeTasksCommand, suggested: true));
        BindVisible(reconnect, nameof(vm.IsTaskReauthorizationRequired), static s => s.IsTaskReauthorizationRequired);
        page.AddGroup(null, null, source, reconnect);
    }
}
