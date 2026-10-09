using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account sub-pages: folder list customization, linked inbox details and per-account unread badges.</summary>
public sealed partial class MacPageRegistry
{
    partial void RegisterAccountExtrasSettingsPages()
    {
        // Parameters as on Windows: the account Guid, or the MergedAccountProviderDetailViewModel.
        Register<FolderCustomizationPageViewController>(WinoPage.FolderCustomizationPage, MacPageHost.SettingsWindow);
        Register<MergedAccountDetailsPageViewController>(WinoPage.MergedAccountDetailsPage, MacPageHost.SettingsWindow);
        Register<AccountUnreadBadgePageViewController>(WinoPage.AccountUnreadBadgePage, MacPageHost.SettingsWindow);
    }
}
