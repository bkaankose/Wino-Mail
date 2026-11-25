using System.Globalization;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// One account's unread badges (Windows AccountUnreadBadgePage) without the taskbar, Dock and tray places: the totals,
/// where badges appear, what the account badge counts, the folders with their Count and Badge switches, and restore.
/// Preference writes are queued by the ViewModel and drained when the page leaves, so the sidebar follows then.
/// </summary>
internal sealed class AccountUnreadBadgePage(AccountUnreadBadgePageViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
    : SettingsPageView<AccountUnreadBadgePageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private Adw.ExpanderRow? _folders;
    private readonly List<Gtk.Widget> _folderRows = [];
    private SettingsBinder? _folderBinder;

    public string? PageTitle => ViewModel.AccountName is { Length: > 0 } name
        ? string.Format(CultureInfo.CurrentCulture, Translator.UnreadBadges_AccountSettingsTitleFormat, name)
        : Translator.UnreadBadges_Title;

    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.HasPendingPreferenceWrites;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Settings.Bind(vm, nameof(vm.AccountName), static s => s.AccountName, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var error = Page.AddBanner();
        Settings.Bind(vm, nameof(vm.PreferenceError), static s => s.PreferenceError, text =>
        {
            error.SetTitle(text ?? string.Empty);
            error.SetRevealed(!string.IsNullOrEmpty(text));
        });

        AddGroup(null, Totals(vm));

        // No taskbar, Dock or tray badge on Linux (owner decision Q10); the place descriptions count those places, so none is shown.
        AddGroup(null, WinoSettingsRows.Expander(Translator.UnreadBadges_Locations_Title, null, WinoIconGlyph.Message, true,
            Settings.Switch(vm, Translator.UnreadBadges_AccountBadge_Title, Translator.UnreadBadges_AccountBadge_Description, WinoIconGlyph.None,
                nameof(vm.IsAccountBadgeEnabled), static s => s.IsAccountBadgeEnabled, static (s, v) => s.IsAccountBadgeEnabled = v),
            Settings.Switch(vm, Translator.UnreadBadges_FolderBadges_Title, Translator.UnreadBadges_FolderBadges_Description, WinoIconGlyph.None,
                nameof(vm.AreFolderBadgesEnabled), static s => s.AreFolderBadgesEnabled, static (s, v) => s.AreFolderBadgesEnabled = v)));

        var source = Settings.Choice(vm, Translator.UnreadBadges_CountSource_Title, null, WinoIconGlyph.None,
            [Translator.UnreadBadges_CountSource_InboxOnly_Title, Translator.UnreadBadges_CountSource_SelectedFolders_Title],
            nameof(vm.IsInboxOnlySource), static s => s.IsInboxOnlySource ? 0 : 1, static (s, v) => s.IsInboxOnlySource = v == 0);
        Settings.Subtitle(source, vm, nameof(vm.IsInboxOnlySource), static s => s.IsInboxOnlySource
            ? Translator.UnreadBadges_CountSource_InboxOnly_Description
            : Translator.UnreadBadges_CountSource_SelectedFolders_Description);
        AddGroup(null, WinoSettingsRows.Expander(Translator.UnreadBadges_CountSource_Title, null, WinoIconGlyph.Folder, true, source));

        _folders = WinoSettingsRows.Expander(Translator.UnreadBadges_Folders_Title, null, WinoIconGlyph.Library, true,
            Settings.Visible(Hint(Translator.UnreadBadges_Folders_InboxOnlyHint, Translator.UnreadBadges_Folders_UseSelectedFolders, vm.UseSelectedFoldersCommand),
                vm, nameof(vm.IsInboxOnlySource), static s => s.IsInboxOnlySource),
            Settings.Visible(Hint(Translator.UnreadBadges_Folders_BadgesOffHint, Translator.UnreadBadges_Folders_TurnOnBadges, vm.TurnOnFolderBadgesCommand),
                vm, nameof(vm.IsFolderBadgesOffHintVisible), static s => s.IsFolderBadgesOffHintVisible));
        WinoSettingsRows.Suffix(_folders, Settings.LabelSuffix(vm, nameof(vm.FolderSummary), static s => s.FolderSummary));
        AddGroup(null, _folders);
        Settings.Collection(vm.Folders, RebuildFolders);

        var restore = WinoSettingsRows.Button(Translator.UnreadBadges_RestoreDefaults);
        var restoreCommand = Bindings.Own(new CommandBinding(vm.RestoreDefaultsCommand, static () => null, restore.SetSensitive, Dispatcher, ReportError));
        Settings.Own(GtkSignals.Connect<Adw.ButtonRow>(h => restore.OnActivated += h, h => restore.OnActivated -= h, (_, _) => restoreCommand.Execute()));
        AddGroup(null, restore);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);

    protected override async Task DeactivateAsync()
    {
        await base.DeactivateAsync();
        await ViewModel.DrainPreferenceWritesAsync();
    }

    private Adw.ActionRow Totals(AccountUnreadBadgePageViewModel vm)
    {
        var row = WinoSettingsRows.Action(string.Empty);
        Settings.Bind(vm, nameof(vm.AccountName), static s => s.AccountName, name => row.SetTitle(name ?? string.Empty));
        Settings.Subtitle(row, vm, nameof(vm.AccountAddress), static s => s.AccountAddress);

        var icon = new WinoAccountIcon(28);
        row.AddPrefix(icon.Root);
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, account =>
        {
            icon.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures);
            icon.AccessibleName = account?.Name;
        });

        var number = Gtk.Label.New("0");
        number.AddCssClass("title-1");
        var caption = Gtk.Label.New(Translator.UnreadBadges_AccountBadgeCaption);
        caption.AddCssClass("caption");
        caption.AddCssClass("dim-label");
        var total = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        total.SetMarginTop(6);
        total.SetMarginBottom(6);
        total.Append(number);
        total.Append(caption);
        Settings.Bind(vm, nameof(vm.AccountUnreadCount), static s => s.AccountUnreadCount, count =>
        {
            number.SetText(count.ToString(CultureInfo.CurrentCulture));
            WinoA11y.Label(total, $"{count} {Translator.UnreadBadges_AccountBadgeCaption}");
        });
        WinoSettingsRows.Suffix(row, total);
        return row;
    }

    private Adw.PreferencesRow Hint(string message, string action, System.Windows.Input.ICommand command)
    {
        var icon = new WinoIcon(WinoIconGlyph.Info, 16).Root;
        icon.AddCssClass("accent");
        icon.SetValign(Gtk.Align.Center);
        var text = WinoSettingsRows.Caption(message);
        text.SetHexpand(true);
        var button = Settings.Button(action, command);
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        box.Append(icon);
        box.Append(text);
        box.Append(button);
        return WinoSettingsRows.Custom(box);
    }

    private void RebuildFolders()
    {
        if (_folders is null) return;
        _folderBinder?.Scope.Dispose();
        foreach (var row in _folderRows) _folders.Remove(row);
        _folderRows.Clear();
        var rows = _folderBinder = Settings.Child();
        foreach (var folder in ViewModel.Folders.ToList())
        {
            var row = WinoSettingsRows.Action(folder.FolderName ?? string.Empty, null, SpecialFolderGlyphs.GetGlyphOrFolder(folder.SpecialFolderType));
            rows.Subtitle(row, folder, nameof(folder.CountDescription), static f => f.IsItemCountFolder
                ? $"{f.CountDescription} · {Translator.UnreadBadges_Folders_ItemCountNote}"
                : f.CountDescription);
            WinoSettingsRows.Suffix(row, Column(Translator.UnreadBadges_Folders_CountColumn,
                rows.Enabled(rows.SwitchSuffix(folder, folder.CountAutomationName, nameof(folder.IsCounted), static f => f.IsCounted, static (f, v) => f.IsCounted = v),
                    folder, nameof(folder.IsCountedEditable), static f => f.IsCountedEditable)));
            WinoSettingsRows.Suffix(row, Column(Translator.UnreadBadges_Folders_BadgeColumn,
                rows.Enabled(rows.SwitchSuffix(folder, folder.BadgeAutomationName, nameof(folder.ShowBadge), static f => f.ShowBadge, static (f, v) => f.ShowBadge = v),
                    folder, nameof(folder.IsBadgeEditable), static f => f.IsBadgeEditable)));
            _folders.AddRow(row);
            _folderRows.Add(row);
        }
    }

    /// <summary>A switch under its column caption, so the folder rows need no separate header row at any width.</summary>
    private static Gtk.Box Column(string caption, Gtk.Switch toggle)
    {
        var label = Gtk.Label.New(caption);
        label.AddCssClass("caption");
        label.AddCssClass("dim-label");
        toggle.SetHalign(Gtk.Align.Center);
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        box.Append(label);
        box.Append(toggle);
        return box;
    }
}
