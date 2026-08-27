#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Selectors;

/// <summary>
/// Maps a navigation menu item to the template that renders it. Templates are looked up by
/// key at selection time rather than wired as properties, so each mode's template dictionary
/// can be merged into the application resources the first time that mode is opened.
/// </summary>
public sealed partial class ShellMenuTemplateSelector : DataTemplateSelector
{
    protected override DataTemplate? SelectTemplateCore(object item) => Resolve(GetTemplateKey(item));

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);

    private static string? GetTemplateKey(object item) => item switch
    {
        // Shared
        SeperatorItem => "SeperatorTemplate",
        ShellSectionHeaderMenuItem => "ShellSectionHeaderTemplate",
        RateMenuItem => "RatingItemTemplate",

        // Settings
        SettingsShellSectionMenuItem => "SettingsShellSectionItemTemplate",
        SettingsShellPageMenuItem settingsShellPageMenuItem => GetSettingsPageTemplateKey(settingsShellPageMenuItem),

        // Calendar
        CalendarDatePickerMenuItem => "CalendarDatePickerTemplate",
        CalendarSyncMenuItem => "CalendarSyncTemplate",
        AccountCalendarGroupMenuItem => "AccountCalendarGroupTemplate",
        NewCalendarEventMenuItem => "CalendarNewEventTemplate",

        // Contacts
        NewContactMenuItem => "NewContactTemplate",
        NewAddressListMenuItem => "NewAddressListTemplate",
        NewTaskListMenuItem => "NewTaskListTemplate",
        NewTaskListGroupMenuItem => "NewTaskListGroupTemplate",
        MyDayTaskMenuItem => "MyDayTaskTemplate",
        PlannedTaskMenuItem => "PlannedTaskTemplate",
        ImportantTaskMenuItem => "ImportantTaskTemplate",
        AccountTaskListAccountMenuItem => "AccountTaskListAccountTemplate",
        AccountTaskListGroupMenuItem => "AccountTaskListGroupTemplate",
        AccountTaskListMenuItem => "AccountTaskListTemplate",
        ContactFilterViewModel { HasAccountIcon: true } => "ContactAccountFilterTemplate",
        ContactFilterViewModel => "ContactFilterTemplate",

        // Mail. NewCalendarEventMenuItem derives from NewMailMenuItem, so it must be
        // matched before this arm is reached.
        NewMailMenuItem => "CreateNewMailTemplate",
        AccountMenuItem => IsCompactAccountMenuEnabled() ? "CompactClickableAccountMenuTemplate" : "ClickableAccountMenuTemplate",
        MergedAccountMenuItem => "MergedAccountTemplate",
        MergedAccountMoreFolderMenuItem => "MergedAccountMoreFolderItemTemplate",
        MergedAccountFolderMenuItem => "MergedAccountFolderMenuItemTemplate",
        MailCategoryMenuItem => "MailCategoryMenuTemplate",
        MergedMailCategoryMenuItem => "MergedMailCategoryMenuTemplate",
        FolderMenuItem => "FolderMenuTemplate",
        FixAccountIssuesMenuItem fixAccountIssuesMenuItem =>
            fixAccountIssuesMenuItem.Account.AttentionReason == AccountAttentionReason.MissingSystemFolderConfiguration
                ? "FixMissingFolderConfigTemplate"
                : "FixAuthenticationIssueTemplate",

        _ => null
    };

    private static string GetSettingsPageTemplateKey(SettingsShellPageMenuItem item)
    {
        if (string.Equals(item.Title, Translator.WinoAccount_SettingsSection_Title, System.StringComparison.Ordinal))
            return "SettingsShellWinoAccountItemTemplate";

        if (string.Equals(item.Title, Translator.WinoIntelligence_SettingsTitle, System.StringComparison.Ordinal))
            return "SettingsShellWinoIntelligenceItemTemplate";

        return "SettingsShellPageItemTemplate";
    }

    private static bool IsCompactAccountMenuEnabled()
        => WinoApplication.Current.Services.GetRequiredService<IPreferencesService>().IsCompactAccountMenuItemEnabled;

    private static DataTemplate? Resolve(string? key)
        => key != null && Application.Current.Resources.TryGetValue(key, out var value)
            ? value as DataTemplate
            : null;
}
