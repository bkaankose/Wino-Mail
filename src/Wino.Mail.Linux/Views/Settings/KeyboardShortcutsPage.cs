using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Keyboard shortcuts (Windows KeyboardShortcutsPage): the Linux single-key switch and the full overview at the top, then
/// every shortcut with its keys, an enabled switch, Edit and Delete, plus Add and Reset to defaults. The main window's
/// router follows every change at once (it listens to the service).
/// </summary>
internal sealed class KeyboardShortcutsPage(KeyboardShortcutsPageViewModel viewModel, WinoViewServices services,
    IKeyboardShortcutService shortcuts, IConfigurationService configuration, SingleKeyShortcuts singleKeys)
    : SettingsPageView<KeyboardShortcutsPageViewModel>(viewModel, services)
{
    private bool _resetChangedShortcuts;

    private WinoSettingsGroup? _list;
    private SettingsBinder? _collection;
    private SettingsBinder? _rows;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        var error = Page.AddBanner();
        Settings.Bind(vm, nameof(vm.ErrorMessage), static s => s.ErrorMessage, text => error.SetTitle(text ?? string.Empty));
        Settings.Bind(vm, nameof(vm.HasError), static s => s.HasError, error.SetRevealed);

        BuildLinuxRows();

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

    private void BuildLinuxRows()
    {
        var single = WinoSettingsRows.Switch(Translator.Linux_SingleKeyShortcuts_Title, Translator.Linux_SingleKeyShortcuts_Description, WinoIconGlyph.Keyboard);
        single.SetActive(singleKeys.IsEnabled);
        Bindings.Own(GtkSignals.OnNotify(single, "active", () =>
        {
            if (single.GetActive() != singleKeys.IsEnabled) singleKeys.Set(single.GetActive());
        }));
        Action changed = () => RunOnUI(() =>
        {
            if (single.GetActive() != singleKeys.IsEnabled) single.SetActive(singleKeys.IsEnabled);
        });
        singleKeys.Changed += changed;
        Bindings.Own(new ActionDisposable(() => singleKeys.Changed -= changed));

        var overview = WinoSettingsRows.Action(Translator.Linux_Shortcuts_ViewAll, Translator.Linux_Shortcuts_ViewAllDescription, WinoIconGlyph.Keyboard);
        var open = WinoSettingsRows.Suffix(overview, WinoSettingsRows.ChevronButton(Translator.Linux_Shortcuts_ViewAll));
        overview.SetActivatableWidget(open);
        Bindings.Own(GtkSignals.OnClicked(open, ShowOverview));
        AddGroup(null, single, overview);

        // Reset to defaults drops the Linux Forward key with every other change; it is seeded again after a reset ran.
        EventHandler shortcutsChanged = (_, _) => _resetChangedShortcuts = true;
        shortcuts.KeyboardShortcutsChanged += shortcutsChanged;
        Bindings.Own(new ActionDisposable(() => shortcuts.KeyboardShortcutsChanged -= shortcutsChanged));
        var reset = ViewModel.ResetToDefaultsCommand;
        PropertyChangedEventHandler resetChanged = (_, args) =>
        {
            if (args.PropertyName != nameof(reset.IsRunning)) return;
            if (reset.IsRunning) _resetChangedShortcuts = false;
            else if (_resetChangedShortcuts) Observe(ShortcutSeeds.SeedAsync(shortcuts, configuration, force: true));
        };
        reset.PropertyChanged += resetChanged;
        Bindings.Own(new ActionDisposable(() => reset.PropertyChanged -= resetChanged));
    }

    private void ShowOverview()
    {
        if (IsReleased || Gio.Application.GetDefault() is not Gtk.Application application) return;
        Dialogs.ShortcutsOverview.Present(Root, shortcuts, application, singleKeys.IsEnabled);
    }

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
