using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Keyboard shortcuts: Add and Reset buttons, then one card per shortcut with the action, the key
/// combination, its mode, an enabled switch and edit/delete buttons (Windows KeyboardShortcutsPage).
/// The recorder sheet comes from the shared dialog service.
/// </summary>
public sealed class KeyboardShortcutsPageViewController(KeyboardShortcutsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<KeyboardShortcutsPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly WinoSettingsGroup _shortcuts = new();
    private BindingScope? _rowScope;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        var add = Bind.Button(Translator.KeyboardShortcuts_Add, vm.StartAddingShortcutCommand, icon: WinoIconGlyph.Add);
        var reset = Bind.Button(Translator.KeyboardShortcuts_ResetToDefaults, vm.ResetToDefaultsCommand, primary: true);
        var buttons = Row(add, reset);
        buttons.EdgeInsets = new NSEdgeInsets(0, 0, 8, 0);
        Add(buttons);

        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, Indeterminate = true, ControlSize = NSControlSize.Small, IsDisplayedWhenStopped = false, TranslatesAutoresizingMaskIntoConstraints = false };
        Add(spinner);
        Bind.Bind(vm, nameof(vm.IsLoading), s => s.IsLoading, loading => { if (loading) spinner.StartAnimation(null); else spinner.StopAnimation(null); });

        var error = InfoBar(WinoInfoBarSeverity.Error, Translator.GeneralTitle_Error, null);
        Bind.Bind(vm, nameof(vm.ErrorMessage), s => s.ErrorMessage, text => { error.Message = text; error.Hidden = string.IsNullOrWhiteSpace(text); });
        Add(error);

        var empty = WinoStyle.Label(Translator.KeyboardShortcuts_Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        Bind.Visible(Add(empty), vm, nameof(vm.IsEmpty), s => s.IsEmpty);

        Add(_shortcuts);
        Bind.Collection(vm.Shortcuts, Rebuild);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);

    private void Rebuild()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        _shortcuts.Clear();

        foreach (var shortcut in ViewModel.Shortcuts.ToList())
        {
            var mode = Caption(shortcut.ModeDisplayName);
            var toggle = new WinoLabeledSwitch { IsOn = shortcut.IsEnabled };
            WinoAccessibility.Label(toggle.Switch, shortcut.ActionDisplayName);
            rows.Bind(shortcut, nameof(shortcut.IsEnabled), s => s.IsEnabled, value => toggle.IsOn = value);
            rows.OnActivated(toggle.Switch, () =>
            {
                shortcut.IsEnabled = toggle.IsOn;
                ViewModel.ToggleShortcutCommand.Execute(shortcut);
            });
            var edit = rows.Button(string.Empty, ViewModel.StartEditingShortcutCommand, () => shortcut, icon: WinoIconGlyph.Edit);
            edit.ToolTip = Translator.Buttons_Edit;
            var delete = rows.Button(string.Empty, ViewModel.DeleteShortcutCommand, () => shortcut, icon: WinoIconGlyph.Delete);
            delete.ToolTip = Translator.Buttons_Delete;
            var card = new WinoSettingsCard(shortcut.ActionDisplayName, shortcut.DisplayName, WinoIconGlyph.Keyboard, Row(mode, toggle, edit, delete));
            _shortcuts.Add(card);
        }
        _shortcuts.Hidden = _shortcuts.RowCount == 0;
    }
}
