using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// The Settings routes (the Mac MacPageRegistry.Settings*.cs). Settings is never a mode: the account avatar, Cmd+, and
/// <c>ChangeApplicationMode(Settings)</c> open the <see cref="WinoPage.SettingsPage"/> root as a full-screen sheet on
/// iPhone and iPad. Every Settings page shares that host, so the first page opens the sheet and later pages push inside
/// it. Both idiom registries call <see cref="RegisterSettingsPages"/> from their constructors.
/// </summary>
public static class SettingsPageRoutes
{
    private static readonly Dictionary<Type, WinoPage> Pages = new();

    /// <summary>Adds every Settings route to the idiom registry as a <see cref="UIKitPageHost.FullScreenSheet"/>.</summary>
    public static UIKitPageRegistry RegisterSettingsPages(this UIKitPageRegistry registry)
    {
        Register(registry, UIKitPageHost.FullScreenSheet);
        return registry;
    }

    /// <summary>The Windows page a Settings controller shows (its first registered route).</summary>
    public static WinoPage? PageFor(Type controller) => Pages.TryGetValue(controller, out var page) ? page : null;

    public static void Register(UIKitPageRegistry registry, UIKitPageHost host)
    {
        // The Windows home grid and the Settings mode open the root list.
        Route<SettingsRootViewController>(registry, WinoPage.SettingsPage, host);
        Route<SettingsRootViewController>(registry, WinoPage.SettingOptionsPage, host);

        // Windows pane order; account sub-pages follow Manage accounts. Companion, Backup and restore and S/MIME have no
        // iOS page (SettingsCatalog.IsVisible, IPlatformCapabilities.Smime).
        Route<AppPreferencesPageViewController>(registry, WinoPage.AppPreferencesPage, host);
        Route<PersonalizationPageViewController>(registry, WinoPage.PersonalizationPage, host);
        Route<ApplicationThemeGalleryPageViewController>(registry, WinoPage.ApplicationThemeGalleryPage, host);
        Route<ApplicationThemeEditorPageViewController>(registry, WinoPage.ApplicationThemeEditorPage, host);
        Route<NotificationSettingsPageViewController>(registry, WinoPage.NotificationSettingsPage, host);
        Route<KeyboardShortcutsPageViewController>(registry, WinoPage.KeyboardShortcutsPage, host);
        Route<AboutPageViewController>(registry, WinoPage.AboutPage, host);
        Route<MailPreferencesPageViewController>(registry, WinoPage.MailPreferencesPage, host);
        Route<MessageListPageViewController>(registry, WinoPage.MessageListPage, host);
        Route<UnreadBadgeSettingsPageViewController>(registry, WinoPage.UnreadBadgeSettingsPage, host);
        Route<AccountUnreadBadgePageViewController>(registry, WinoPage.AccountUnreadBadgePage, host);
        Route<ReadComposePanePageViewController>(registry, WinoPage.ReadComposePanePage, host);
        Route<EmailTemplatesPageViewController>(registry, WinoPage.EmailTemplatesPage, host);
        Route<CreateEmailTemplatePageViewController>(registry, WinoPage.CreateEmailTemplatePage, host);
        Route<StoragePageViewController>(registry, WinoPage.StoragePage, host);
        Route<CalendarPreferenceSettingsPageViewController>(registry, WinoPage.CalendarPreferenceSettingsPage, host);
        Route<CalendarPreferenceSettingsPageViewController>(registry, WinoPage.CalendarSettingsPage, host);
        Route<CalendarRenderingSettingsPageViewController>(registry, WinoPage.CalendarRenderingSettingsPage, host);
        Route<ContactsPreferenceSettingsPageViewController>(registry, WinoPage.ContactsPreferenceSettingsPage, host);
        Route<ToDoPreferenceSettingsPageViewController>(registry, WinoPage.ToDoPreferenceSettingsPage, host);
        Route<ManageAccountsPageViewController>(registry, WinoPage.ManageAccountsPage, host);
        Route<ManageAccountsPageViewController>(registry, WinoPage.AccountManagementPage, host);
        Route<AccountDetailsPageViewController>(registry, WinoPage.AccountDetailsPage, host);
        Route<MergedAccountDetailsPageViewController>(registry, WinoPage.MergedAccountDetailsPage, host);
        Route<AliasManagementPageViewController>(registry, WinoPage.AliasManagementPage, host);
        Route<MailCategoryManagementPageViewController>(registry, WinoPage.MailCategoryManagementPage, host);
        Route<SignatureManagementPageViewController>(registry, WinoPage.SignatureManagementPage, host);
        Route<FolderCustomizationPageViewController>(registry, WinoPage.FolderCustomizationPage, host);
        Route<MailFiltersPageViewController>(registry, WinoPage.MailFiltersPage, host);
        Route<MailFilterEditorPageViewController>(registry, WinoPage.MailFilterEditorPage, host);
        Route<CalendarAccountSettingsPageViewController>(registry, WinoPage.CalendarAccountSettingsPage, host);
        Route<WinoAccountManagementPageViewController>(registry, WinoPage.WinoAccountManagementPage, host);
        Route<WinoIntelligencePageViewController>(registry, WinoPage.WinoIntelligencePage, host);
        Route<WinoIntelligenceManagementPageViewController>(registry, WinoPage.WinoIntelligenceManagementPage, host);
        Route<IntelligenceCoveragePageViewController>(registry, WinoPage.IntelligenceCoveragePage, host);
    }

    private static void Route<TController>(UIKitPageRegistry registry, WinoPage page, UIKitPageHost host) where TController : UIViewController
    {
        registry.Register<TController>(page, host);
        Pages.TryAdd(typeof(TController), page);
    }
}
