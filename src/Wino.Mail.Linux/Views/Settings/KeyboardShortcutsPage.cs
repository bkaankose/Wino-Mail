using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Keyboard shortcuts (Windows KeyboardShortcutsPage): every shortcut with its keys, an enabled switch, Edit and Delete,
/// plus Add and Reset to defaults. The main window's router follows every change at once (it listens to the service).
/// </summary>
internal sealed class KeyboardShortcutsPage(KeyboardShortcutsPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<KeyboardShortcutsPageViewModel>(viewModel, services)
{
    private WinoSettingsGroup? _list;
    private SettingsBinder? _collection;
    private SettingsBinder? _rows;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var error = Page.AddBanner();
        Settings.Bind(vm, nameof(vm.ErrorMessage), static s => s.ErrorMessage, text => error.SetTitle(text ?? string.Empty));
        Settings.Bind(vm, nameof(vm.HasError), static s => s.HasError, error.SetRevealed);

        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        actions.Append(Settings.Button(Translator.KeyboardShortcuts_Add, vm.StartAddingShortcutCommand, suggested: true));
        actions.Append(Settings.Button(Translator.KeyboardShortcuts_ResetToDefaults, vm.ResetToDefaultsCommand));
        _list = Page.AddGroup(Translator.Settings_KeyboardShortcuts_Title, Translator.KeyboardShortcuts_PageDescription);
        _list.HidesWhenEmpty = false;
        _list.HeaderSuffix = actions;

        var loading = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        loading.SetHalign(Gtk.Align.Center);
        loading.Append(Adw.Spinner.New());
        var empty = Adw.StatusPage.New();
        empty.SetTitle(Translator.KeyboardShortcuts_Empty);
        empty.AddCssClass("compact");
        var states = Page.AddGroup(null, null,
            Settings.Visible(WinoSettingsRows.Custom(loading), vm, nameof(vm.IsLoading), static s => s.IsLoading),
            Settings.Visible(WinoSettingsRows.Custom(empty, Translator.KeyboardShortcuts_Empty), vm, nameof(vm.IsEmpty), static s => s.IsEmpty));
        Settings.Visible(states.Root, vm, nameof(vm.IsLoading), static s => s.IsLoading || s.IsEmpty);
        Settings.Visible(states.Root, vm, nameof(vm.IsEmpty), static s => s.IsLoading || s.IsEmpty);

        Settings.Bind(vm, nameof(vm.Shortcuts), static s => s.Shortcuts, collection =>
        {
            _collection?.Scope.Dispose();
            _collection = Settings.Child();
            if (collection is not null) _collection.Collection(collection, Rebuild);
            Rebuild();
        });
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);

    private void Rebuild()
    {
        if (_list is null || IsReleased) return;
        _rows?.Scope.Dispose();
        _list.Clear();
        var rows = _rows = Settings.Child();
        var vm = ViewModel;
        foreach (var shortcut in vm.Shortcuts.ToList())
        {
            if (shortcut is null) continue;
            var title = shortcut.ActionDisplayName ?? string.Empty;
            var named = $"{title}, {shortcut.ModeDisplayName}";
            var row = WinoSettingsRows.Action(title, shortcut.ModeDisplayName, WinoIconGlyph.Keyboard);
            WinoSettingsRows.Suffix(row, ShortcutDisplay.Create(shortcut.ModifierKeys, shortcut.Key));
            WinoSettingsRows.Suffix(row, rows.SwitchSuffix(shortcut, string.Format(Translator.Accessibility_ShortcutEnabled, named),
                nameof(KeyboardShortcutViewModel.IsEnabled), static s => s.IsEnabled, (s, enabled) =>
                {
                    if (s.IsEnabled == enabled) return;
                    s.IsEnabled = enabled;
                    vm.ToggleShortcutCommand.Execute(s);
                }));
            var edit = WinoSettingsRows.Suffix(row, rows.Button(Translator.Buttons_Edit, vm.StartEditingShortcutCommand, () => shortcut));
            edit.AddCssClass("flat");
            WinoA11y.Rename(edit, $"{Translator.Buttons_Edit} {named}");
            var delete = WinoSettingsRows.Suffix(row, rows.Button(Translator.Buttons_Delete, vm.DeleteShortcutCommand, () => shortcut, destructive: true));
            delete.AddCssClass("flat");
            WinoA11y.Rename(delete, $"{Translator.Buttons_Delete} {named}");
            _list.Add(row);
        }
    }
}
