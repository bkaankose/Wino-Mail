#nullable enable

using System;
using System.Collections.Generic;
using MailCal.Calendar.Views;
using MailCal.Core.Domain.Enums;
using MailCal.Mail.WinUI.Views;
using MailCal.Mail.WinUI.Views.Calendar;
using MailCal.Views;
using MailCal.Views.Account;
using MailCal.Views.Mail;
using MailCal.Views.Settings;
using MailCal.Views.ToDo;

namespace MailCal.Mail.WinUI.Navigation;

/// <summary>
/// Single source of truth for every navigable page: its view type, owning mode, default
/// frame and navigation role.
/// </summary>
public static class NavigationRouteTable
{
    private static readonly NavigationRoute[] Routes =
    [
        // ---- Mail ----------------------------------------------------------------
        Root(MailCalPage.MailListPage, typeof(MailListPage), MailCalApplicationMode.Mail),
        Rendering(MailCalPage.MailRenderingPage, typeof(MailRenderingPage), MailCalApplicationMode.Mail),
        Rendering(MailCalPage.ComposePage, typeof(ComposePage), MailCalApplicationMode.Mail),
        Rendering(MailCalPage.TestPage, typeof(TestPage), MailCalApplicationMode.Mail),
        Rendering(MailCalPage.IdlePage, typeof(IdlePage), mode: null),

        // ---- Calendar ------------------------------------------------------------
        Root(MailCalPage.CalendarPage, typeof(CalendarPage), MailCalApplicationMode.Calendar),
        Detail(MailCalPage.EventDetailsPage, typeof(EventDetailsPage), MailCalApplicationMode.Calendar),
        Detail(MailCalPage.CalendarEventComposePage, typeof(CalendarEventComposePage), MailCalApplicationMode.Calendar),

        // ---- Contacts ------------------------------------------------------------
        Root(MailCalPage.ContactsPage, typeof(ContactsPage), MailCalApplicationMode.Contacts),
        Detail(MailCalPage.ContactEditPage, typeof(ContactEditPage), MailCalApplicationMode.Contacts),

        // ---- To Do --------------------------------------------------------------
        Root(MailCalPage.ToDoPage, typeof(ToDoPage), MailCalApplicationMode.Tasks),

        // ---- Settings ------------------------------------------------------------
        Root(MailCalPage.SettingsPage, typeof(SettingsPage), MailCalApplicationMode.Settings),
        Settings(MailCalPage.SettingOptionsPage, typeof(SettingOptionsPage)),
        Settings(MailCalPage.ManageAccountsPage, typeof(AccountManagementPage)),
        Settings(MailCalPage.AccountManagementPage, typeof(AccountManagementPage)),
        Settings(MailCalPage.AccountDetailsPage, typeof(AccountDetailsPage)),
        Settings(MailCalPage.MergedAccountDetailsPage, typeof(MergedAccountDetailsPage)),
        Settings(MailCalPage.FolderCustomizationPage, typeof(FolderCustomizationPage)),
        Settings(MailCalPage.SignatureManagementPage, typeof(SignatureManagementPage)),
        Settings(MailCalPage.SignatureAndEncryptionPage, typeof(SignatureAndEncryptionPage)),
        Settings(MailCalPage.AboutPage, typeof(AboutPage)),
        Settings(MailCalPage.PersonalizationPage, typeof(PersonalizationPage)),
        Settings(MailCalPage.ApplicationThemeGalleryPage, typeof(ApplicationThemeGalleryPage)),
        Settings(MailCalPage.ApplicationThemeEditorPage, typeof(ApplicationThemeEditorPage)),
        Settings(MailCalPage.MessageListPage, typeof(MessageListPage)),
        Settings(MailCalPage.NotificationSettingsPage, typeof(NotificationSettingsPage)),
        Settings(MailCalPage.UnreadBadgeSettingsPage, typeof(UnreadBadgeSettingsPage)),
        Settings(MailCalPage.AccountUnreadBadgePage, typeof(AccountUnreadBadgePage)),
        Settings(MailCalPage.ReadComposePanePage, typeof(ReadComposePanePage)),
        Settings(MailCalPage.AppPreferencesPage, typeof(AppPreferencesPage)),
        Settings(MailCalPage.CompanionSettingsPage, typeof(CompanionSettingsPage)),
        Settings(MailCalPage.MailPreferencesPage, typeof(MailPreferencesPage)),
        Settings(MailCalPage.BackupRestorePage, typeof(BackupRestorePage)),
        Settings(MailCalPage.AliasManagementPage, typeof(AliasManagementPage)),
        Settings(MailCalPage.MailCategoryManagementPage, typeof(MailCategoryManagementPage)),
        Settings(MailCalPage.MailFiltersPage, typeof(MailFiltersPage)),
        Settings(MailCalPage.MailFilterEditorPage, typeof(MailFilterEditorPage)),
        Settings(MailCalPage.ImapCalDavSettingsPage, typeof(ImapCalDavSettingsPage)),
        Settings(MailCalPage.KeyboardShortcutsPage, typeof(KeyboardShortcutsPage)),
        Settings(MailCalPage.EmailTemplatesPage, typeof(EmailTemplatesPage)),
        Settings(MailCalPage.CreateEmailTemplatePage, typeof(CreateEmailTemplatePage)),
        Settings(MailCalPage.StoragePage, typeof(StoragePage)),
        Settings(MailCalPage.CalendarSettingsPage, typeof(CalendarPreferenceSettingsPage)),
        Settings(MailCalPage.CalendarPreferenceSettingsPage, typeof(CalendarPreferenceSettingsPage)),
        Settings(MailCalPage.CalendarRenderingSettingsPage, typeof(CalendarRenderingSettingsPage)),
        Settings(MailCalPage.CalendarAccountSettingsPage, typeof(CalendarAccountSettingsPage)),
        Settings(MailCalPage.ContactsPreferenceSettingsPage, typeof(ContactsPreferenceSettingsPage)),
        Settings(MailCalPage.ToDoPreferenceSettingsPage, typeof(ToDoPreferenceSettingsPage)),

        // ---- Account setup wizard ------------------------------------------------
        new(MailCalPage.WelcomeHostPage, typeof(WelcomeHostPage), null, NavigationReferenceFrame.ShellFrame, RouteKind.Standalone),
        Wizard(MailCalPage.WelcomePageV2, typeof(WelcomePageV2)),
        Wizard(MailCalPage.ProviderSelectionPage, typeof(ProviderSelectionPage)),
        Wizard(MailCalPage.AccountSetupProgressPage, typeof(AccountSetupProgressPage)),
        Wizard(MailCalPage.SpecialImapCredentialsPage, typeof(SpecialImapCredentialsPage))
    ];

    private static readonly Dictionary<MailCalPage, NavigationRoute> RoutesByPage = BuildPageIndex();
    private static readonly Dictionary<Type, NavigationRoute> RoutesByType = BuildTypeIndex();

    public static IReadOnlyList<NavigationRoute> All => Routes;

    public static NavigationRoute? Find(MailCalPage page)
        => RoutesByPage.TryGetValue(page, out var route) ? route : null;

    /// <summary>
    /// Reverse lookup for back stack entries, which only carry the view type.
    /// Pages sharing a view type resolve to the first declared route.
    /// </summary>
    public static NavigationRoute? Find(Type? pageType)
        => pageType != null && RoutesByType.TryGetValue(pageType, out var route) ? route : null;

    public static Type? GetPageType(MailCalPage page) => Find(page)?.PageType;

    public static bool IsAllowedIn(MailCalApplicationMode mode, Type? pageType)
        => Find(pageType)?.IsAllowedIn(mode) == true;

    private static NavigationRoute Root(MailCalPage page, Type pageType, MailCalApplicationMode mode)
        => new(page, pageType, mode, NavigationReferenceFrame.InnerShellFrame, RouteKind.ModeRoot);

    private static NavigationRoute Detail(MailCalPage page, Type pageType, MailCalApplicationMode mode)
        => new(page, pageType, mode, NavigationReferenceFrame.InnerShellFrame, RouteKind.Detail);

    private static NavigationRoute Rendering(MailCalPage page, Type pageType, MailCalApplicationMode? mode)
        => new(page, pageType, mode, NavigationReferenceFrame.RenderingFrame, RouteKind.Rendering);

    private static NavigationRoute Settings(MailCalPage page, Type pageType)
        => new(page, pageType, MailCalApplicationMode.Settings, NavigationReferenceFrame.InnerShellFrame, RouteKind.Hosted);

    private static NavigationRoute Wizard(MailCalPage page, Type pageType)
        => new(page, pageType, null, NavigationReferenceFrame.ShellFrame, RouteKind.Hosted);

    private static Dictionary<MailCalPage, NavigationRoute> BuildPageIndex()
    {
        var index = new Dictionary<MailCalPage, NavigationRoute>(Routes.Length);

        foreach (var route in Routes)
        {
            index[route.Page] = route;
        }

        return index;
    }

    private static Dictionary<Type, NavigationRoute> BuildTypeIndex()
    {
        var index = new Dictionary<Type, NavigationRoute>(Routes.Length);

        foreach (var route in Routes)
        {
            // Some logical pages share a view type (ManageAccountsPage / AccountManagementPage).
            // The first declaration wins so the reverse lookup stays deterministic.
            if (!index.ContainsKey(route.PageType))
            {
                index.Add(route.PageType, route);
            }
        }

        return index;
    }
}
