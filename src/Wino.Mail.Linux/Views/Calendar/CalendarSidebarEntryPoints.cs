using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Sidebar menu entries of the calendar rows (macOS CalendarAccountSettingsEntryPoints). The extension sees the row,
/// not the pointer, so a group row lists its account settings and one settings entry per calendar as a submenu.
/// </summary>
internal static class CalendarSidebarEntryPoints
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        ShellSidebarContextMenus.AddExtension(Entries);
    }

    private static SidebarMenuEntry[] Entries(IMenuItem item, ShellSidebarMenuContext context)
    {
        switch (item)
        {
            case UngroupedCalendarMenuItem { Parameter.AccountCalendar: { } calendar }:
                return [CalendarSettings(Translator.CalendarAccountSettings_Title + "…", calendar, context)];
            case AccountCalendarGroupMenuItem { Parameter: { Account: { } account } group }:
                var calendars = group.AccountCalendars.Select(entry => CalendarSettings(entry.Name, entry.AccountCalendar, context)).ToArray();
                SidebarMenuEntry[] entries =
                [
                    new(Translator.AccountContextMenu_ManageAccountSettings, () => Open(context, () => context.Navigation.ChangeApplicationMode(WinoApplicationMode.Settings,
                        new ShellModeActivationContext
                        {
                            Parameter = new SettingsPageActivationContext(WinoPage.ManageAccountsPage, new AccountDetailsNavigationContext(account.Id, AccountDetailsTab.Calendar)),
                            SuppressStartupFlows = true
                        })), Glyph: WinoIconGlyph.ManageAccounts),
                    new(Translator.CalendarAccountSettings_Title + "…", null, calendars.Length > 0, Glyph: WinoIconGlyph.Settings, Children: calendars)
                ];
                return entries;
            default:
                return [];
        }
    }

    private static SidebarMenuEntry CalendarSettings(string title, Wino.Core.Domain.Entities.Calendar.AccountCalendar calendar, ShellSidebarMenuContext context)
        => new(title, () => Open(context, () => context.Navigation.Navigate(WinoPage.CalendarAccountSettingsPage, calendar)), Glyph: WinoIconGlyph.Settings);

    private static Task Open(ShellSidebarMenuContext context, Func<bool> navigate)
    {
        if (!navigate()) Serilog.Log.Information("Calendar settings are not available.");
        return Task.CompletedTask;
    }
}
