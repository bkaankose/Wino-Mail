#nullable enable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.MenuItems;
using MailCal.Mail.ViewModels.Data;

namespace MailCal.Mail.WinUI.Selectors;

/// <summary>
/// Maps a navigation menu item to one of the templates supplied by the shell's resource dictionary.
/// </summary>
public sealed partial class ShellMenuTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SeperatorTemplate { get; set; }
    public DataTemplate? ShellSectionHeaderTemplate { get; set; }
    public DataTemplate? SettingsShellSectionItemTemplate { get; set; }
    public DataTemplate? SettingsShellGroupItemTemplate { get; set; }
    public DataTemplate? SettingsShellPageItemTemplate { get; set; }
    public DataTemplate? CalendarDatePickerTemplate { get; set; }
    public DataTemplate? AccountCalendarGroupTemplate { get; set; }
    public DataTemplate? UngroupedCalendarTemplate { get; set; }
    public DataTemplate? CalendarNewEventTemplate { get; set; }
    public DataTemplate? NewContactTemplate { get; set; }
    public DataTemplate? NewAddressListTemplate { get; set; }
    public DataTemplate? NewTaskListTemplate { get; set; }
    public DataTemplate? MyDayTaskTemplate { get; set; }
    public DataTemplate? PlannedTaskTemplate { get; set; }
    public DataTemplate? ImportantTaskTemplate { get; set; }
    public DataTemplate? SharedAccountMenuTemplate { get; set; }
    public DataTemplate? SharedCompactAccountMenuTemplate { get; set; }
    public DataTemplate? AccountTaskListGroupTemplate { get; set; }
    public DataTemplate? AccountTaskListTemplate { get; set; }
    public DataTemplate? ContactFilterTemplate { get; set; }
    public DataTemplate? ContactCategoryFilterTemplate { get; set; }
    public DataTemplate? ContactCategoriesExpanderTemplate { get; set; }
    public DataTemplate? CreateNewMailTemplate { get; set; }
    public DataTemplate? MergedAccountTemplate { get; set; }
    public DataTemplate? MergedAccountMoreFolderItemTemplate { get; set; }
    public DataTemplate? MergedAccountFolderMenuItemTemplate { get; set; }
    public DataTemplate? MailCategoryMenuTemplate { get; set; }
    public DataTemplate? MergedMailCategoryMenuTemplate { get; set; }
    public DataTemplate? FolderMenuTemplate { get; set; }
    public DataTemplate? FixMissingFolderConfigTemplate { get; set; }
    public DataTemplate? FixAuthenticationIssueTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => GetTemplate(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);

    private DataTemplate? GetTemplate(object item) => item switch
    {
        // Shared
        SeperatorItem => SeperatorTemplate,
        ShellSectionHeaderMenuItem => ShellSectionHeaderTemplate,

        // Settings
        SettingsShellSectionMenuItem => SettingsShellSectionItemTemplate,
        SettingsShellGroupMenuItem => SettingsShellGroupItemTemplate,
        SettingsShellPageMenuItem settingsShellPageMenuItem => GetSettingsPageTemplate(settingsShellPageMenuItem),

        // Calendar
        CalendarDatePickerMenuItem => CalendarDatePickerTemplate,
        AccountCalendarGroupMenuItem => AccountCalendarGroupTemplate,
        CalendarAccountMenuItem => SharedAccountMenuTemplate,
        UngroupedCalendarMenuItem => UngroupedCalendarTemplate,
        NewCalendarEventMenuItem => CalendarNewEventTemplate,

        // Contacts
        NewContactMenuItem => NewContactTemplate,
        NewAddressListMenuItem => NewAddressListTemplate,
        NewTaskListMenuItem => NewTaskListTemplate,
        MyDayTaskMenuItem => MyDayTaskTemplate,
        PlannedTaskMenuItem => PlannedTaskTemplate,
        ImportantTaskMenuItem => ImportantTaskTemplate,
        AccountTaskListAccountMenuItem => GetAccountTemplate(),
        AccountTaskListGroupMenuItem => AccountTaskListGroupTemplate,
        AccountTaskListMenuItem => AccountTaskListTemplate,
        ContactCategoriesExpanderMenuItem => ContactCategoriesExpanderTemplate,
        ContactFilterViewModel { HasAccountIcon: true } => GetAccountTemplate(),
        ContactFilterViewModel { IsCategory: true } => ContactCategoryFilterTemplate,
        ContactFilterViewModel => ContactFilterTemplate,

        // Mail. NewCalendarEventMenuItem derives from NewMailMenuItem, so it must be
        // matched before this arm is reached.
        NewMailMenuItem => CreateNewMailTemplate,
        AccountMenuItem => GetAccountTemplate(),
        MergedAccountMenuItem => MergedAccountTemplate,
        MergedAccountMoreFolderMenuItem => MergedAccountMoreFolderItemTemplate,
        MergedAccountFolderMenuItem => MergedAccountFolderMenuItemTemplate,
        MailCategoryMenuItem => MailCategoryMenuTemplate,
        MergedMailCategoryMenuItem => MergedMailCategoryMenuTemplate,
        FolderMenuItem => FolderMenuTemplate,
        FixAccountIssuesMenuItem fixAccountIssuesMenuItem =>
            fixAccountIssuesMenuItem.Account.AttentionReason == AccountAttentionReason.MissingSystemFolderConfiguration
                ? FixMissingFolderConfigTemplate
                : FixAuthenticationIssueTemplate,

        _ => null
    };

    private DataTemplate? GetSettingsPageTemplate(SettingsShellPageMenuItem item)
    {
        return SettingsShellPageItemTemplate;
    }

    private static bool IsCompactAccountMenuEnabled()
        => MailCalApplication.Current.Services.GetRequiredService<IPreferencesService>().IsCompactAccountMenuItemEnabled;

    private DataTemplate? GetAccountTemplate()
        => IsCompactAccountMenuEnabled() ? SharedCompactAccountMenuTemplate : SharedAccountMenuTemplate;
}
