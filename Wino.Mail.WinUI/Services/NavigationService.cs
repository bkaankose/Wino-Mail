using System;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Calendar.Views;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Views.Calendar;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Navigation;
using Wino.Views;
using Wino.Views.Account;
using Wino.Views.Mail;
using Wino.Views.Settings;

namespace Wino.Services;

public class NavigationService : NavigationServiceBase, INavigationService
{
    private readonly IStatePersistanceService _statePersistanceService;
    private readonly IDispatcher _dispatcher;

    private WinoPage[] _renderingPageTypes = new WinoPage[]
    {
        WinoPage.MailRenderingPage,
        WinoPage.ComposePage
    };

    private static readonly WinoPage[] MailOnlyPages =
    [
        WinoPage.MailListPage,
        WinoPage.MailRenderingPage,
        WinoPage.ComposePage,
        WinoPage.IdlePage,
        WinoPage.WelcomePage
    ];

    private static readonly WinoPage[] CalendarOnlyPages =
    [
        WinoPage.CalendarPage,
        WinoPage.EventDetailsPage
    ];

    public NavigationService(IStatePersistanceService statePersistanceService, IDispatcher dispatcher)
    {
        _statePersistanceService = statePersistanceService;
        _dispatcher = dispatcher;
    }

    private bool IsOnNavigationThread()
        => _dispatcher is WinUIDispatcher winUiDispatcher && winUiDispatcher.HasThreadAccess;

    private T ExecuteOnNavigationThread<T>(Func<T> action)
    {
        if (IsOnNavigationThread())
            return action();

        T result = default!;
        _dispatcher.ExecuteOnUIThread(() => result = action()).GetAwaiter().GetResult();
        return result;
    }

    private void ExecuteOnNavigationThread(Action action)
    {
        if (IsOnNavigationThread())
        {
            action();
            return;
        }

        _dispatcher.ExecuteOnUIThread(action).GetAwaiter().GetResult();
    }

    public Type? GetPageType(WinoPage winoPage)
    {
        return winoPage switch
        {
            WinoPage.None => null,
            WinoPage.IdlePage => typeof(IdlePage),
            WinoPage.AccountDetailsPage => typeof(AccountDetailsPage),
            WinoPage.MergedAccountDetailsPage => typeof(MergedAccountDetailsPage),
            WinoPage.AccountManagementPage => typeof(AccountManagementPage),
            WinoPage.ManageAccountsPage => typeof(ManageAccountsPage),
            WinoPage.SignatureManagementPage => typeof(SignatureManagementPage),
            WinoPage.AboutPage => typeof(AboutPage),
            WinoPage.PersonalizationPage => typeof(PersonalizationPage),
            WinoPage.MessageListPage => typeof(MessageListPage),
            WinoPage.ReadComposePanePage => typeof(ReadComposePanePage),
            WinoPage.MailRenderingPage => typeof(MailRenderingPage),
            WinoPage.ComposePage => typeof(ComposePage),
            WinoPage.MailListPage => typeof(MailListPage),
            WinoPage.SettingsPage => typeof(SettingsPage),
            WinoPage.WelcomePage => typeof(WelcomePage),
            WinoPage.SettingOptionsPage => typeof(SettingOptionsPage),
            WinoPage.AppPreferencesPage => typeof(AppPreferencesPage),
            WinoPage.AliasManagementPage => typeof(AliasManagementPage),
            WinoPage.LanguageTimePage => typeof(LanguageTimePage),
            WinoPage.EditAccountDetailsPage => typeof(EditAccountDetailsPage),
            WinoPage.ImapCalDavSettingsPage => typeof(ImapCalDavSettingsPage),
            WinoPage.KeyboardShortcutsPage => typeof(KeyboardShortcutsPage),
            WinoPage.ContactsPage => typeof(ContactsPage),
            WinoPage.SignatureAndEncryptionPage => typeof(SignatureAndEncryptionPage),
            WinoPage.StoragePage => typeof(StoragePage),
            WinoPage.CalendarPage => typeof(CalendarPage),
            WinoPage.EventDetailsPage => typeof(EventDetailsPage),
            WinoPage.CalendarSettingsPage => typeof(CalendarSettingsPage),
            WinoPage.CalendarAccountSettingsPage => typeof(CalendarAccountSettingsPage),
            _ => null,
        };
    }

    public Frame GetCoreFrame(NavigationReferenceFrame frameType)
        => ExecuteOnNavigationThread(() => GetCoreFrameInternal(frameType));

    private Frame GetCoreFrameInternal(NavigationReferenceFrame frameType)
    {
        if (WinoApplication.MainWindow is not IWinoShellWindow shellWindow) throw new ArgumentException("MainWindow must implement IWinoShellWindow");
        if (shellWindow.GetMainFrame() is not Frame mainFrame) throw new ArgumentException("MainFrame cannot be null.");

        if (frameType == NavigationReferenceFrame.ShellFrame) return shellWindow.GetMainFrame();

        var contentRoot = mainFrame.Content as UIElement;
        if (contentRoot == null) return mainFrame;

        return WinoVisualTreeHelper.GetChildObject<Frame>(contentRoot, frameType.ToString()) ?? mainFrame;
    }

    public bool ChangeApplicationMode(WinoApplicationMode mode)
        => ExecuteOnNavigationThread(() => ChangeApplicationModeInternal(mode));

    private bool ChangeApplicationModeInternal(WinoApplicationMode mode)
    {
        var coreFrame = GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame);

        if (coreFrame == null) return false;

        // Update the application mode in state persistence service
        _statePersistanceService.ApplicationMode = mode;

        var targetPageType = mode == WinoApplicationMode.Mail ? typeof(MailAppShell) : typeof(CalendarAppShell);
        var currentPageType = coreFrame.Content?.GetType();
        var transitionInfo = GetNavigationTransitionInfo(NavigationTransitionType.DrillIn);

        // If already on the target page, do nothing
        if (currentPageType == targetPageType)
            return true;

        // Check if we can go back to the target page
        if (coreFrame.CanGoBack && coreFrame.BackStack.Count > 0)
        {
            var previousPage = coreFrame.BackStack[coreFrame.BackStack.Count - 1];
            if (previousPage.SourcePageType == targetPageType)
            {
                coreFrame.GoBack(transitionInfo);
                return true;
            }
        }

        // Check if we can go forward to the target page
        if (coreFrame.CanGoForward && coreFrame.ForwardStack.Count > 0)
        {
            var nextPage = coreFrame.ForwardStack[coreFrame.ForwardStack.Count - 1];
            if (nextPage.SourcePageType == targetPageType)
            {
                coreFrame.GoForward();
                return true;
            }
        }

        // Navigate to the target page only if it's not in the navigation stack
        coreFrame.Navigate(targetPageType, null, transitionInfo);
        return true;
    }

    public bool Navigate(WinoPage page,
                         object? parameter = null,
                         NavigationReferenceFrame frame = NavigationReferenceFrame.InnerShellFrame,
                         NavigationTransitionType transition = NavigationTransitionType.None)
        => ExecuteOnNavigationThread(() => NavigateInternal(page, parameter, frame, transition));

    private bool NavigateInternal(WinoPage page,
                                  object? parameter = null,
                                  NavigationReferenceFrame frame = NavigationReferenceFrame.InnerShellFrame,
                                  NavigationTransitionType transition = NavigationTransitionType.None)
    {
        var pageType = GetPageType(page);
        if (pageType == null) return false;

        var currentApplicationMode = _statePersistanceService.ApplicationMode;

        if (currentApplicationMode == WinoApplicationMode.Calendar && IsMailOnlyPage(page))
        {
            return false;
        }

        if (currentApplicationMode == WinoApplicationMode.Mail && IsCalendarOnlyPage(page))
        {
            return false;
        }

        _statePersistanceService.IsReadingMail = _renderingPageTypes.Contains(page);
        _statePersistanceService.IsEventDetailsVisible = page == WinoPage.EventDetailsPage;

        Frame innerShellFrame = GetCoreFrameInternal(NavigationReferenceFrame.InnerShellFrame);

        if (innerShellFrame != null)
        {
            // Calendar navigations.
            if (currentApplicationMode == WinoApplicationMode.Calendar)
            {
                var currentFrameType = GetCurrentFrameType(ref innerShellFrame);

                if (page == WinoPage.CalendarPage &&
                    parameter is CalendarPageNavigationArgs calendarNavigationArgs)
                {
                    var loadCalendarMessage = CreateLoadCalendarMessage(calendarNavigationArgs);

                    // Date changes while CalendarPage is already active should not re-navigate the frame.
                    if (currentFrameType == pageType)
                    {
                        WeakReferenceMessenger.Default.Send(loadCalendarMessage);
                        return true;
                    }

                    // If CalendarPage is the previous page, reuse it instead of creating a second instance.
                    var lastBackStackEntry = innerShellFrame.BackStack.Count > 0 ? innerShellFrame.BackStack[^1] : null;
                    if (innerShellFrame.CanGoBack && lastBackStackEntry?.SourcePageType == pageType)
                    {
                        innerShellFrame.GoBack();
                        WeakReferenceMessenger.Default.Send(loadCalendarMessage);
                        return true;
                    }
                }

                return innerShellFrame.Navigate(pageType, parameter);
            }
            else
            {
                // Mail navigations.
                var currentFrameType = GetCurrentFrameType(ref innerShellFrame);
                bool isMailListingPageActive = currentFrameType != null && currentFrameType == typeof(MailListPage);

                // Active page is mail list page and we are refreshing the folder.
                if (isMailListingPageActive && currentFrameType == pageType && parameter is NavigateMailFolderEventArgs folderNavigationArgs)
                {
                    // No need for new navigation, just refresh the folder.
                    WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folderNavigationArgs.BaseFolderMenuItem, folderNavigationArgs.FolderInitLoadAwaitTask));
                    WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

                    return true;
                }

                var transitionInfo = GetNavigationTransitionInfo(transition);

                // This page must be opened in the Frame placed in MailListingPage.
                if (isMailListingPageActive && frame == NavigationReferenceFrame.RenderingFrame)
                {
                    var listingFrame = GetCoreFrameInternal(NavigationReferenceFrame.RenderingFrame);
                    if (listingFrame == null) return false;

                    // Active page is mail list page and we are opening a mail item.
                    // No navigation needed, just refresh the rendered mail item.
                    if (listingFrame.Content != null
                        && listingFrame.Content.GetType() == GetPageType(WinoPage.MailRenderingPage)
                        && parameter is MailItemViewModel mailItemViewModel
                        && page != WinoPage.ComposePage)
                    {
                        WeakReferenceMessenger.Default.Send(new ReaderItemRefreshRequestedEvent(mailItemViewModel));
                    }
                    else if (listingFrame.Content != null
                        && listingFrame.Content.GetType() == GetPageType(WinoPage.ComposePage)
                        && page == WinoPage.ComposePage
                        && parameter is MailItemViewModel composeDraftViewModel)
                    {
                        // ComposePage is already active and we're switching to another draft.
                        // Reuse existing ComposePage and WebView2 instead of navigating.
                        WeakReferenceMessenger.Default.Send(new ReaderItemRefreshRequestedEvent(composeDraftViewModel));
                    }
                    else if (listingFrame.Content != null
                        && listingFrame.Content.GetType() == GetPageType(WinoPage.IdlePage)
                        && pageType == typeof(IdlePage))
                    {
                        // Idle -> Idle navigation. Ignore.
                        return true;
                    }
                    else
                    {
                        listingFrame.Navigate(pageType, parameter, transitionInfo);
                    }

                    return true;
                }

                if ((currentFrameType != null && currentFrameType != pageType) || currentFrameType == null)
                {
                    return innerShellFrame.Navigate(pageType, parameter, transitionInfo);
                }
            }
        }

        return false;
    }

    private static bool IsMailOnlyPage(WinoPage page)
        => MailOnlyPages.Contains(page);

    private static bool IsCalendarOnlyPage(WinoPage page)
        => CalendarOnlyPages.Contains(page);

    private static LoadCalendarMessage CreateLoadCalendarMessage(CalendarPageNavigationArgs args)
    {
        var targetDate = args.RequestDefaultNavigation
            ? DateTime.Now.Date
            : args.NavigationDate;

        var initiative = args.RequestDefaultNavigation
            ? CalendarInitInitiative.App
            : CalendarInitInitiative.User;

        return new LoadCalendarMessage(targetDate, initiative);
    }

    public void GoBack(Core.Domain.Enums.NavigationTransitionEffect slideEffect = Core.Domain.Enums.NavigationTransitionEffect.FromRight)
        => ExecuteOnNavigationThread(() => GoBackInternal(slideEffect));

    private void GoBackInternal(Core.Domain.Enums.NavigationTransitionEffect slideEffect = Core.Domain.Enums.NavigationTransitionEffect.FromRight)
    {
        // Check if we're navigating within ManageAccountsPage (applies to both modes)
        // Check if we're navigating within SettingsPage (applies to both modes)
        if (_statePersistanceService.IsManageAccountsNavigating || _statePersistanceService.IsSettingsNavigating)
        {
            // Send message to ManageAccountsPage to go back within its AccountPagesFrame
            WeakReferenceMessenger.Default.Send(new BackBreadcrumNavigationRequested(slideEffect));
            return;
        }

        var innerShellFrame = GetCoreFrameInternal(NavigationReferenceFrame.InnerShellFrame);

        if (_statePersistanceService.ApplicationMode == WinoApplicationMode.Calendar && innerShellFrame?.CanGoBack == true)
        {
            innerShellFrame.GoBack();

            // Calendar mode: Navigate back from EventDetailsPage
            _statePersistanceService.IsEventDetailsVisible = false;
        }
        else
        {
            if (_statePersistanceService.IsReadingMail && _statePersistanceService.IsReaderNarrowed)
            {
                // Mail mode: Clear selections and dispose rendering frame
                _statePersistanceService.IsReadingMail = false;

                WeakReferenceMessenger.Default.Send(new ClearMailSelectionsRequested());
                WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
            }
            else if (innerShellFrame != null && innerShellFrame.CanGoBack)
            {
                innerShellFrame.GoBack();
            }
        }
    }

    // Standalone EML viewer.
    //public void NavigateRendering(MimeMessageInformation mimeMessageInformation, NavigationTransitionType transition = NavigationTransitionType.None)
    //{
    //    if (mimeMessageInformation == null)
    //        throw new ArgumentException("MimeMessage cannot be null.");

    //    Navigate(WinoPage.MailRenderingPage, mimeMessageInformation, NavigationReferenceFrame.RenderingFrame, transition);
    //}

    //// Mail item view model clicked handler.
    //public void NavigateRendering(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None)
    //{
    //    if (mailItem is MailItemViewModel mailItemViewModel)
    //        Navigate(WinoPage.MailRenderingPage, mailItemViewModel, NavigationReferenceFrame.RenderingFrame, transition);
    //    else
    //        throw new ArgumentException("MailItem must be of type MailItemViewModel.");
    //}

    //public void NavigateFolder(NavigateMailFolderEventArgs args)
    //    => Navigate(WinoPage.MailListPage, args, NavigationReferenceFrame.ShellFrame);
}
