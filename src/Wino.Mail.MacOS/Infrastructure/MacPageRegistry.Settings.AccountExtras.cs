using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account sub-pages: folder list customization, account group details and per-account unread badges.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterAccountExtrasSettingsPages()
    {
        // Parameters as on Windows: the account Guid, or an AccountGroupDetailsNavigationContext (or the group's MergedAccountProviderDetailViewModel).
        Register<FolderCustomizationPageViewController>(WinoPage.FolderCustomizationPage, MacPageHost.SettingsWindow);
        Register<AccountGroupDetailsPageViewController>(WinoPage.MergedAccountDetailsPage, MacPageHost.SettingsWindow);
        Register<AccountUnreadBadgePageViewController>(WinoPage.AccountUnreadBadgePage, MacPageHost.SettingsWindow);
    }
}
