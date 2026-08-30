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
/// Maps a navigation menu item to one of the templates supplied by the shell's resource dictionary.
/// </summary>
public sealed partial class ShellMenuTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SeperatorTemplate { get; set; }
    public DataTemplate? ShellSectionHeaderTemplate { get; set; }
    public DataTemplate? RatingItemTemplate { get; set; }
    public DataTemplate? SettingsShellSectionItemTemplate { get; set; }
    public DataTemplate? SettingsShellGroupItemTemplate { get; set; }
    public DataTemplate? SettingsShellPageItemTemplate { get; set; }
    public DataTemplate? SettingsShellWinoAccountItemTemplate { get; set; }
    public DataTemplate? SettingsShellWinoIntelligenceItemTemplate { get; set; }
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
        RateMenuItem => RatingItemTemplate,

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
        ContactFilterViewModel { HasAccountIcon: true } => GetAccountTemplate(),
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
        if (string.Equals(item.Title, Translator.WinoAccount_SettingsSection_Title, System.StringComparison.Ordinal))
            return SettingsShellWinoAccountItemTemplate;

        if (string.Equals(item.Title, Translator.WinoIntelligence_SettingsTitle, System.StringComparison.Ordinal))
            return SettingsShellWinoIntelligenceItemTemplate;

        return SettingsShellPageItemTemplate;
    }

    private static bool IsCompactAccountMenuEnabled()
        => WinoApplication.Current.Services.GetRequiredService<IPreferencesService>().IsCompactAccountMenuItemEnabled;

    private DataTemplate? GetAccountTemplate()
        => IsCompactAccountMenuEnabled() ? SharedCompactAccountMenuTemplate : SharedAccountMenuTemplate;
}
