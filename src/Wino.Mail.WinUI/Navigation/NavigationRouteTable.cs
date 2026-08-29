#nullable enable

using System;
using System.Collections.Generic;
using Wino.Calendar.Views;
using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI.Views;
using Wino.Mail.WinUI.Views.Calendar;
using Wino.Views;
using Wino.Views.Account;
using Wino.Views.Mail;
using Wino.Views.Settings;
using Wino.Views.ToDo;

namespace Wino.Mail.WinUI.Navigation;

/// <summary>
/// Single source of truth for every navigable page: its view type, owning mode, default
/// frame and navigation role.
/// </summary>
public static class NavigationRouteTable
{
    private static readonly NavigationRoute[] Routes =
    [
        // ---- Mail ----------------------------------------------------------------
        Root(WinoPage.MailListPage, typeof(MailListPage), WinoApplicationMode.Mail),
        Rendering(WinoPage.MailRenderingPage, typeof(MailRenderingPage), WinoApplicationMode.Mail),
        Rendering(WinoPage.ComposePage, typeof(ComposePage), WinoApplicationMode.Mail),
        Rendering(WinoPage.TestPage, typeof(TestPage), WinoApplicationMode.Mail),
        Rendering(WinoPage.IdlePage, typeof(IdlePage), mode: null),

        // ---- Calendar ------------------------------------------------------------
        Root(WinoPage.CalendarPage, typeof(CalendarPage), WinoApplicationMode.Calendar),
        Detail(WinoPage.EventDetailsPage, typeof(EventDetailsPage), WinoApplicationMode.Calendar),
        Detail(WinoPage.CalendarEventComposePage, typeof(CalendarEventComposePage), WinoApplicationMode.Calendar),

        // ---- Contacts ------------------------------------------------------------
        Root(WinoPage.ContactsPage, typeof(ContactsPage), WinoApplicationMode.Contacts),
        Detail(WinoPage.ContactEditPage, typeof(ContactEditPage), WinoApplicationMode.Contacts),

        // ---- To Do --------------------------------------------------------------
        Root(WinoPage.ToDoPage, typeof(ToDoPage), WinoApplicationMode.Tasks),

        // ---- Settings ------------------------------------------------------------
        Root(WinoPage.SettingsPage, typeof(SettingsPage), WinoApplicationMode.Settings),
        Settings(WinoPage.SettingOptionsPage, typeof(SettingOptionsPage)),
        Settings(WinoPage.ManageAccountsPage, typeof(AccountManagementPage)),
        Settings(WinoPage.AccountManagementPage, typeof(AccountManagementPage)),
        Settings(WinoPage.AccountDetailsPage, typeof(AccountDetailsPage)),
        Settings(WinoPage.MergedAccountDetailsPage, typeof(MergedAccountDetailsPage)),
        Settings(WinoPage.FolderCustomizationPage, typeof(FolderCustomizationPage)),
        Settings(WinoPage.SignatureManagementPage, typeof(SignatureManagementPage)),
        Settings(WinoPage.SignatureAndEncryptionPage, typeof(SignatureAndEncryptionPage)),
        Settings(WinoPage.AboutPage, typeof(AboutPage)),
        Settings(WinoPage.PersonalizationPage, typeof(PersonalizationPage)),
        Settings(WinoPage.MessageListPage, typeof(MessageListPage)),
        Settings(WinoPage.MailNotificationSettingsPage, typeof(MailNotificationSettingsPage)),
        Settings(WinoPage.ReadComposePanePage, typeof(ReadComposePanePage)),
        Settings(WinoPage.AppPreferencesPage, typeof(AppPreferencesPage)),
        Settings(WinoPage.MailPreferencesPage, typeof(MailPreferencesPage)),
        Settings(WinoPage.BackupRestorePage, typeof(BackupRestorePage)),
        Settings(WinoPage.AliasManagementPage, typeof(AliasManagementPage)),
        Settings(WinoPage.MailCategoryManagementPage, typeof(MailCategoryManagementPage)),
        Settings(WinoPage.MailFiltersPage, typeof(MailFiltersPage)),
        Settings(WinoPage.MailFilterEditorPage, typeof(MailFilterEditorPage)),
        Settings(WinoPage.ImapCalDavSettingsPage, typeof(ImapCalDavSettingsPage)),
        Settings(WinoPage.KeyboardShortcutsPage, typeof(KeyboardShortcutsPage)),
        Settings(WinoPage.EmailTemplatesPage, typeof(EmailTemplatesPage)),
        Settings(WinoPage.CreateEmailTemplatePage, typeof(CreateEmailTemplatePage)),
        Settings(WinoPage.StoragePage, typeof(StoragePage)),
        Settings(WinoPage.WinoAccountManagementPage, typeof(WinoAccountManagementPage)),
        Settings(WinoPage.WinoIntelligencePage, typeof(WinoIntelligencePage)),
        Settings(WinoPage.WinoIntelligenceManagementPage, typeof(WinoIntelligenceManagementPage)),
        Settings(WinoPage.IntelligenceCoveragePage, typeof(IntelligenceCoveragePage)),
        Settings(WinoPage.CalendarSettingsPage, typeof(CalendarPreferenceSettingsPage)),
        Settings(WinoPage.CalendarPreferenceSettingsPage, typeof(CalendarPreferenceSettingsPage)),
        Settings(WinoPage.CalendarRenderingSettingsPage, typeof(CalendarRenderingSettingsPage)),
        Settings(WinoPage.CalendarNotificationSettingsPage, typeof(CalendarNotificationSettingsPage)),
        Settings(WinoPage.CalendarAccountSettingsPage, typeof(CalendarAccountSettingsPage)),
        Settings(WinoPage.ContactsPreferenceSettingsPage, typeof(ContactsPreferenceSettingsPage)),
        Settings(WinoPage.ToDoPreferenceSettingsPage, typeof(ToDoPreferenceSettingsPage)),

        // ---- Account setup wizard ------------------------------------------------
        new(WinoPage.WelcomeHostPage, typeof(WelcomeHostPage), null, NavigationReferenceFrame.ShellFrame, RouteKind.Standalone),
        Wizard(WinoPage.WelcomePageV2, typeof(WelcomePageV2)),
        Wizard(WinoPage.ProviderSelectionPage, typeof(ProviderSelectionPage)),
        Wizard(WinoPage.AccountSetupProgressPage, typeof(AccountSetupProgressPage)),
        Wizard(WinoPage.SpecialImapCredentialsPage, typeof(SpecialImapCredentialsPage))
    ];

    private static readonly Dictionary<WinoPage, NavigationRoute> RoutesByPage = BuildPageIndex();
    private static readonly Dictionary<Type, NavigationRoute> RoutesByType = BuildTypeIndex();

    public static IReadOnlyList<NavigationRoute> All => Routes;

    public static NavigationRoute? Find(WinoPage page)
        => RoutesByPage.TryGetValue(page, out var route) ? route : null;

    /// <summary>
    /// Reverse lookup for back stack entries, which only carry the view type.
    /// Pages sharing a view type resolve to the first declared route.
    /// </summary>
    public static NavigationRoute? Find(Type? pageType)
        => pageType != null && RoutesByType.TryGetValue(pageType, out var route) ? route : null;

    public static Type? GetPageType(WinoPage page) => Find(page)?.PageType;

    public static bool IsAllowedIn(WinoApplicationMode mode, Type? pageType)
        => Find(pageType)?.IsAllowedIn(mode) == true;

    private static NavigationRoute Root(WinoPage page, Type pageType, WinoApplicationMode mode)
        => new(page, pageType, mode, NavigationReferenceFrame.InnerShellFrame, RouteKind.ModeRoot);

    private static NavigationRoute Detail(WinoPage page, Type pageType, WinoApplicationMode mode)
        => new(page, pageType, mode, NavigationReferenceFrame.InnerShellFrame, RouteKind.Detail);

    private static NavigationRoute Rendering(WinoPage page, Type pageType, WinoApplicationMode? mode)
        => new(page, pageType, mode, NavigationReferenceFrame.RenderingFrame, RouteKind.Rendering);

    private static NavigationRoute Settings(WinoPage page, Type pageType)
        => new(page, pageType, WinoApplicationMode.Settings, NavigationReferenceFrame.InnerShellFrame, RouteKind.Hosted);

    private static NavigationRoute Wizard(WinoPage page, Type pageType)
        => new(page, pageType, null, NavigationReferenceFrame.ShellFrame, RouteKind.Hosted);

    private static Dictionary<WinoPage, NavigationRoute> BuildPageIndex()
    {
        var index = new Dictionary<WinoPage, NavigationRoute>(Routes.Length);

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
