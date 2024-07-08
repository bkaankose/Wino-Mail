using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.MenuItems;

namespace Wino.Selectors
{
    public class NavigationMenuTemplateSelector : DataTemplateSelector
    {
        public DataTemplate MenuItemTemplate { get; set; }
        public DataTemplate AccountManagementTemplate { get; set; }
        public DataTemplate ClickableAccountMenuTemplate { get; set; }
        public DataTemplate MergedAccountTemplate { get; set; }
        public DataTemplate MergedAccountFolderTemplate { get; set; }
        public DataTemplate MergedAccountMoreExpansionItemTemplate { get; set; }
        public DataTemplate FolderMenuTemplate { get; set; }
        public DataTemplate SettingsItemTemplate { get; set; }
        public DataTemplate MoreItemsFolderTemplate { get; set; }
        public DataTemplate RatingItemTemplate { get; set; }
        public DataTemplate CreateNewFolderTemplate { get; set; }
        public DataTemplate SeperatorTemplate { get; set; }
        public DataTemplate NewMailTemplate { get; set; }
        public DataTemplate CategoryItemsTemplate { get; set; }
        public DataTemplate FixAuthenticationIssueTemplate { get; set; }
        public DataTemplate FixMissingFolderConfigTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is NewMailMenuItem)
                return NewMailTemplate;
            else if (item is SettingsItem)
                return SettingsItemTemplate;
            else if (item is SeperatorItem)
                return SeperatorTemplate;
            else if (item is AccountMenuItem accountMenuItem)
                // Merged inbox account menu items must be nested.
                return ClickableAccountMenuTemplate;
            else if (item is ManageAccountsMenuItem)
                return AccountManagementTemplate;
            else if (item is RateMenuItem)
                return RatingItemTemplate;
            else if (item is MergedAccountMenuItem)
                return MergedAccountTemplate;
            else if (item is MergedAccountMoreFolderMenuItem)
                return MergedAccountMoreExpansionItemTemplate;
            else if (item is MergedAccountFolderMenuItem)
                return MergedAccountFolderTemplate;
            else if (item is FolderMenuItem)
                return FolderMenuTemplate;
            else if (item is FixAccountIssuesMenuItem fixAccountIssuesMenuItem)
                return fixAccountIssuesMenuItem.Account.AttentionReason == Core.Domain.Enums.AccountAttentionReason.MissingSystemFolderConfiguration
                        ? FixMissingFolderConfigTemplate : FixAuthenticationIssueTemplate;
            else
            {
                var type = item.GetType();
                return null;
            }
        }
    }
}
