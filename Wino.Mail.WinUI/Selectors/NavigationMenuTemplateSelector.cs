using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.MenuItems;

namespace Wino.Mail.WinUI.Selectors;

public partial class NavigationMenuTemplateSelector : DataTemplateSelector
{
    public DataTemplate MenuItemTemplate { get; set; } = null!;
    public DataTemplate ContactsMenuItemTemplate { get; set; } = null!;
    public DataTemplate ClickableAccountMenuTemplate { get; set; } = null!;
    public DataTemplate MergedAccountTemplate { get; set; } = null!;
    public DataTemplate MergedAccountFolderTemplate { get; set; } = null!;
    public DataTemplate MergedAccountMoreExpansionItemTemplate { get; set; } = null!;
    public DataTemplate FolderMenuTemplate { get; set; } = null!;
    public DataTemplate SettingsItemTemplate { get; set; } = null!;
    public DataTemplate SettingsShellPageItemTemplate { get; set; } = null!;
    public DataTemplate SettingsShellSectionItemTemplate { get; set; } = null!;
    public DataTemplate WinoAccountSettingsShellPageItemTemplate { get; set; } = null!;
    public DataTemplate StoreUpdateItemTemplate { get; set; } = null!;
    public DataTemplate MoreItemsFolderTemplate { get; set; } = null!;
    public DataTemplate RatingItemTemplate { get; set; } = null!;
    public DataTemplate CreateNewFolderTemplate { get; set; } = null!;
    public DataTemplate SeperatorTemplate { get; set; } = null!;
    public DataTemplate NewMailTemplate { get; set; } = null!;
    public DataTemplate CalendarNewEventTemplate { get; set; } = null!;
    public DataTemplate CategoryItemsTemplate { get; set; } = null!;
    public DataTemplate MergedCategoryItemsTemplate { get; set; } = null!;
    public DataTemplate FixAuthenticationIssueTemplate { get; set; } = null!;
    public DataTemplate FixMissingFolderConfigTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is NewCalendarEventMenuItem)
            return CalendarNewEventTemplate;
        else if (item is NewMailMenuItem)
            return NewMailTemplate;
        else if (item is ContactsMenuItem)
            return ContactsMenuItemTemplate;
        else if (item is SettingsItem)
            return SettingsItemTemplate;
        else if (item is SettingsShellPageMenuItem settingsShellPageMenuItem)
            return string.Equals(settingsShellPageMenuItem.Title, Translator.WinoAccount_SettingsSection_Title, System.StringComparison.Ordinal)
                ? WinoAccountSettingsShellPageItemTemplate
                : SettingsShellPageItemTemplate;
        else if (item is SettingsShellSectionMenuItem)
            return SettingsShellSectionItemTemplate;
        else if (item is StoreUpdateMenuItem)
            return StoreUpdateItemTemplate;
        else if (item is SeperatorItem)
            return SeperatorTemplate;
        else if (item is AccountMenuItem)
            // Merged inbox account menu items must be nested.
            return ClickableAccountMenuTemplate;
        else if (item is RateMenuItem)
            return RatingItemTemplate;
        else if (item is MergedAccountMenuItem)
            return MergedAccountTemplate;
        else if (item is MergedAccountMoreFolderMenuItem)
            return MergedAccountMoreExpansionItemTemplate;
        else if (item is MailCategoryMenuItem)
            return CategoryItemsTemplate;
        else if (item is MergedMailCategoryMenuItem)
            return MergedCategoryItemsTemplate;
        else if (item is MergedAccountFolderMenuItem)
            return MergedAccountFolderTemplate;
        else if (item is FolderMenuItem)
            return FolderMenuTemplate;
        else if (item is FixAccountIssuesMenuItem fixAccountIssuesMenuItem)
            return fixAccountIssuesMenuItem.Account.AttentionReason == Wino.Core.Domain.Enums.AccountAttentionReason.MissingSystemFolderConfiguration
                    ? FixMissingFolderConfigTemplate : FixAuthenticationIssueTemplate;
        return MenuItemTemplate;
    }
}


