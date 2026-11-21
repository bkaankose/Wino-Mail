using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.ViewModels.Shell;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The Keyboard shortcuts dialog: the application accelerators, then the enabled shortcuts of each mode. Built fresh on
/// every open from the snapshot of that moment, so it holds no subscription.
/// </summary>
internal static class ShortcutsOverview
{
    public static void Present(Gtk.Widget owner, IKeyboardShortcutService shortcuts, Gtk.Application application)
    {
        var dialog = Adw.ShortcutsDialog.New();

        var general = Adw.ShortcutsSection.New(AppActions.ApplicationName);
        AddAction(general, application, Translator.MenuSettings, AppActions.Preferences);
        AddAction(general, application, Translator.Settings_KeyboardShortcuts_Title, AppActions.Shortcuts);
        AddAction(general, application, Translator.MacOSMenu_ToggleSidebar, AppActions.ToggleSidebar);
        AddAction(general, application, Translator.Buttons_Back, AppActions.Back);
        foreach (var mode in AppModeVisibility.SwitchableModes)
            AddAction(general, application, ShellModeActivator.ModeTitle(mode), ShellModeActions.Name(mode));
        AddAction(general, application, string.Format(Translator.MacOSMenu_Quit, AppActions.ApplicationName), AppActions.Quit);
        dialog.Add(general);

        foreach (var mode in AppModeVisibility.SwitchableModes)
        {
            Adw.ShortcutsSection? section = null;
            foreach (var shortcut in shortcuts.EnabledShortcutsSnapshot.Where(item => item.Mode == mode).OrderBy(item => item.CreatedAt))
            {
                if (ShortcutKeys.Accelerator(shortcut.ModifierKeys, shortcut.Key) is not { } accelerator) continue;
                section ??= Adw.ShortcutsSection.New(ShellModeActivator.ModeTitle(mode));
                section.Add(Adw.ShortcutsItem.New(ActionTitle(mode, shortcut.Action), accelerator));
            }
            if (section is not null) dialog.Add(section);
        }

        dialog.Present(owner);
    }

    public static string ActionTitle(WinoApplicationMode mode, KeyboardShortcutAction action)
        => new KeyboardShortcutActionViewModel(mode, action).DisplayName;

    private static void AddAction(Adw.ShortcutsSection section, Gtk.Application application, string title, string name)
    {
        if (application.LookupAction(name) is null || application.GetAccelsForAction(AppActions.Detailed(name)).Length == 0) return;
        section.Add(Adw.ShortcutsItem.NewFromAction(title, AppActions.Detailed(name)));
    }
}
