using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.ViewModels.Shell;

namespace Wino.Mail.Linux.Views.Dialogs;

/// <summary>
/// The Keyboard shortcuts dialog: General and Navigation from <see cref="LinuxKeymap"/>, each mode's enabled configurable
/// shortcuts with its fixed keys, the composer, and the single keys while they are on. Rows marked as widget keys are
/// keys GTK widgets handle themselves (lists, menus, the editor); they are listed so the dialog is complete. Built fresh
/// on every open from the snapshot of that moment, so it holds no subscription.
/// </summary>
internal static class ShortcutsOverview
{
    public static void Present(Gtk.Widget owner, IKeyboardShortcutService shortcuts, Gtk.Application application, bool singleKeys)
    {
        var dialog = Adw.ShortcutsDialog.New();

        var general = Adw.ShortcutsSection.New(AppActions.ApplicationName);
        AddKeys(general, application, LinuxKeyGroup.General);
        dialog.Add(general);

        var navigation = Adw.ShortcutsSection.New(Translator.Linux_Shortcuts_Navigation);
        AddKeys(navigation, application, LinuxKeyGroup.Navigation);
        navigation.Add(Adw.ShortcutsItem.New(Translator.Linux_Shortcuts_OpenItem, "Return Right"));
        navigation.Add(Adw.ShortcutsItem.New(Translator.Linux_Shortcuts_ContextMenu, "Menu <Shift>F10"));
        navigation.Add(Adw.ShortcutsItem.New(Translator.Linux_Shortcuts_PrimaryMenu, "F10"));
        dialog.Add(navigation);

        foreach (var mode in AppModeVisibility.SwitchableModes)
        {
            var section = Adw.ShortcutsSection.New(ShellModeActivator.ModeTitle(mode));
            var count = 0;
            foreach (var shortcut in shortcuts.EnabledShortcutsSnapshot.Where(item => item.Mode == mode).OrderBy(item => item.CreatedAt))
            {
                if (ShortcutKeys.Accelerator(shortcut.ModifierKeys, shortcut.Key) is not { } accelerator) continue;
                section.Add(Adw.ShortcutsItem.New(ActionTitle(mode, shortcut.Action), accelerator));
                count++;
            }
            switch (mode)
            {
                case WinoApplicationMode.Mail:
                    section.Add(Adw.ShortcutsItem.New(Translator.Linux_Shortcuts_DeletePermanently, "<Shift>Delete"));
                    count += 1 + AddKeys(section, application, LinuxKeyGroup.Mail) + AddKeys(section, application, LinuxKeyGroup.Reader);
                    section.Add(Adw.ShortcutsItem.New(Translator.MacOSMenu_SelectAll, "<Control>a"));
                    section.Add(Adw.ShortcutsItem.New(Translator.MacOSMenu_Undo, "<Control>z"));
                    section.Add(Adw.ShortcutsItem.New(Translator.Linux_Shortcuts_Thread, "Left Right"));
                    count += 3;
                    break;
                case WinoApplicationMode.Calendar:
                    count += AddKeys(section, application, LinuxKeyGroup.Calendar);
                    section.Add(Adw.ShortcutsItem.New(Translator.Linux_Shortcuts_CalendarPeriod, "Left Right"));
                    count++;
                    break;
                case WinoApplicationMode.Tasks:
                    count += AddKeys(section, application, LinuxKeyGroup.Tasks);
                    break;
            }
            if (count > 0) dialog.Add(section);
        }

        var composer = Adw.ShortcutsSection.New(Translator.Linux_Shortcuts_Composer);
        AddKeys(composer, application, LinuxKeyGroup.Composer);
        composer.Add(Adw.ShortcutsItem.New(Translator.Composer_Bold, "<Control>b"));
        composer.Add(Adw.ShortcutsItem.New(Translator.Composer_Italic, "<Control>i"));
        composer.Add(Adw.ShortcutsItem.New(Translator.Composer_Underline, "<Control>u"));
        composer.Add(Adw.ShortcutsItem.New(Translator.Composer_InsertLink, "<Control>k"));
        composer.Add(Adw.ShortcutsItem.New(Translator.MailOperation_Find, "<Control>f"));
        composer.Add(Adw.ShortcutsItem.New(Translator.Composer_Replace, "<Control>h"));
        dialog.Add(composer);

        if (singleKeys)
        {
            var single = Adw.ShortcutsSection.New(Translator.Linux_SingleKeyShortcuts_Title);
            AddKeys(single, application, LinuxKeyGroup.SingleKey);
            dialog.Add(single);
        }

        dialog.Present(owner);
    }

    public static string ActionTitle(WinoApplicationMode mode, KeyboardShortcutAction action)
        => new KeyboardShortcutActionViewModel(mode, action).DisplayName;

    /// <summary>Every key of the group as the keymap names it; a Global key shows the accelerators the application has now.</summary>
    private static int AddKeys(Adw.ShortcutsSection section, Gtk.Application application, LinuxKeyGroup group)
    {
        var count = 0;
        foreach (var key in LinuxKeymap.All)
        {
            if (key.Group != group) continue;
            var accelerators = key.Scope == LinuxKeyScope.Global && application.LookupAction(key.Action) is not null
                ? application.GetAccelsForAction(AppActions.Detailed(key.Action))
                : key.Accelerators;
            if (accelerators.Length == 0) continue;
            section.Add(Adw.ShortcutsItem.New(key.Title(), string.Join(' ', accelerators)));
            count++;
        }
        return count;
    }
}
