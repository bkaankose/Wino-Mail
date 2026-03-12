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
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Views;
using Wino.Mail.WinUI.Views.Calendar;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Navigation;
using Wino.Views;
using Wino.Views.Account;
using Wino.Views.Mail;
using Wino.Views.Settings;
using Microsoft.UI.Xaml.Media.Animation;

namespace Wino.Services;

public class NavigationService : NavigationServiceBase, INavigationService
{
    private readonly IStatePersistanceService _statePersistanceService;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoWindowManager _windowManager;
    private NavigationTransitionInfo? _pendingInnerShellTransition;

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
        WinoPage.WelcomePageV2,
        WinoPage.WelcomeHostPage,
        WinoPage.ProviderSelectionPage,
        WinoPage.AccountSetupProgressPage,
        WinoPage.SpecialImapCredentialsPage
    ];

    private static readonly WinoPage[] CalendarOnlyPages =
    [
        WinoPage.CalendarPage,
        WinoPage.EventDetailsPage,
        WinoPage.CalendarEventComposePage
    ];

    private static readonly WinoPage[] ContactsOnlyPages =
    [
        WinoPage.ContactsPage
    ];

    public NavigationService(IStatePersistanceService statePersistanceService, IDispatcher dispatcher, IWinoWindowManager windowManager)
    {
        _statePersistanceService = statePersistanceService;
        _dispatcher = dispatcher;
        _windowManager = windowManager;
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
            WinoPage.ManageAccountsPage => typeof(AccountManagementPage),
            WinoPage.SignatureManagementPage => typeof(SignatureManagementPage),
            WinoPage.AboutPage => typeof(AboutPage),
            WinoPage.PersonalizationPage => typeof(PersonalizationPage),
            WinoPage.MessageListPage => typeof(MessageListPage),
            WinoPage.ReadComposePanePage => typeof(ReadComposePanePage),
            WinoPage.MailRenderingPage => typeof(MailRenderingPage),
            WinoPage.ComposePage => typeof(ComposePage),
            WinoPage.MailListPage => typeof(MailListPage),
            WinoPage.SettingsPage => typeof(SettingsPage),
            WinoPage.WelcomePageV2 => typeof(WelcomePageV2),
            WinoPage.SettingOptionsPage => typeof(SettingOptionsPage),
            WinoPage.AppPreferencesPage => typeof(AppPreferencesPage),
            WinoPage.AliasManagementPage => typeof(AliasManagementPage),
            WinoPage.LanguageTimePage => typeof(LanguageTimePage),
            WinoPage.ImapCalDavSettingsPage => typeof(ImapCalDavSettingsPage),
            WinoPage.KeyboardShortcutsPage => typeof(KeyboardShortcutsPage),
            WinoPage.ContactsPage => typeof(ContactsPage),
            WinoPage.SignatureAndEncryptionPage => typeof(SignatureAndEncryptionPage),
            WinoPage.EmailTemplatesPage => typeof(EmailTemplatesPage),
            WinoPage.CreateEmailTemplatePage => typeof(CreateEmailTemplatePage),
            WinoPage.StoragePage => typeof(StoragePage),
            WinoPage.WelcomeHostPage => typeof(WelcomeHostPage),
            WinoPage.ProviderSelectionPage => typeof(ProviderSelectionPage),
            WinoPage.AccountSetupProgressPage => typeof(AccountSetupProgressPage),
            WinoPage.SpecialImapCredentialsPage => typeof(SpecialImapCredentialsPage),
            WinoPage.CalendarPage => typeof(CalendarPage),
            WinoPage.EventDetailsPage => typeof(EventDetailsPage),
            WinoPage.CalendarEventComposePage => typeof(CalendarEventComposePage),
            WinoPage.CalendarSettingsPage => typeof(CalendarSettingsPage),
            WinoPage.CalendarAccountSettingsPage => typeof(CalendarAccountSettingsPage),
            _ => null,
        };
    }

    public Frame GetCoreFrame(NavigationReferenceFrame frameType)
        => ExecuteOnNavigationThread(() => GetCoreFrameInternal(frameType) ?? throw new ArgumentException($"Frame '{frameType}' cannot be resolved."));

    private Frame? GetCoreFrameInternal(NavigationReferenceFrame frameType, WinoWindowKind? requestedWindowKind = null)
    {
        if (frameType == NavigationReferenceFrame.ShellFrame)
        {
            if (requestedWindowKind.HasValue)
                return _windowManager.GetPrimaryNavigationFrame(requestedWindowKind.Value);

            var activeWindow = _windowManager.ActiveWindow;
            if (activeWindow != null)
            {
                var activeShellWindow = _windowManager.GetWindow(WinoWindowKind.Shell);
                if (ReferenceEquals(activeWindow, activeShellWindow))
                    return _windowManager.GetPrimaryNavigationFrame(WinoWindowKind.Shell);

                var activeWelcomeWindow = _windowManager.GetWindow(WinoWindowKind.Welcome);
                if (ReferenceEquals(activeWindow, activeWelcomeWindow))
                    return _windowManager.GetPrimaryNavigationFrame(WinoWindowKind.Welcome);
            }

            return _windowManager.GetPrimaryNavigationFrame(WinoWindowKind.Shell)
                ?? _windowManager.GetPrimaryNavigationFrame(WinoWindowKind.Welcome);
        }

        var mainFrame = _windowManager.GetPrimaryNavigationFrame(WinoWindowKind.Shell);
        if (mainFrame == null)
            return null;

        var contentRoot = mainFrame.Content as FrameworkElement;
        if (contentRoot == null) return null;

        // Use FindName first — it works immediately after InitializeComponent(),
        // before the visual tree is built by the layout pass.
        if (contentRoot.FindName(frameType.ToString()) is Frame namedFrame)
            return namedFrame;

        // Fall back to visual tree search for deeply nested frames (e.g. RenderingFrame).
        return WinoVisualTreeHelper.GetChildObject<Frame>(contentRoot, frameType.ToString());
    }

    public bool ChangeApplicationMode(WinoApplicationMode mode)
        => ExecuteOnNavigationThread(() => ChangeApplicationModeInternal(mode));

    private bool ChangeApplicationModeInternal(WinoApplicationMode mode)
    {
        var coreFrame = GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame);

        if (coreFrame == null) return false;

        var currentMode = _statePersistanceService.ApplicationMode;
        var isInitialShellNavigation = coreFrame.Content is not IShellHost;

        // Update the application mode in state persistence service
        _statePersistanceService.ApplicationMode = mode;
        _statePersistanceService.AppModeTitle = GetApplicationModeTitle(mode);

        if (coreFrame.Content is IShellHost activeShell && activeShell.HasShellContent && currentMode == mode)
            return true;

        _pendingInnerShellTransition = isInitialShellNavigation
            ? null
            : GetApplicationModeTransitionInfo(currentMode, mode);

        if (coreFrame.Content is not IShellHost)
        {
            coreFrame.BackStack.Clear();
            coreFrame.ForwardStack.Clear();
            coreFrame.Navigate(typeof(WinoAppShell), null, new SuppressNavigationTransitionInfo());
        }

        if (coreFrame.Content is IShellHost shell)
        {
            shell.ActivateMode(mode, new ShellModeActivationContext
            {
                IsInitialActivation = isInitialShellNavigation
            });
            return true;
        }

        _pendingInnerShellTransition = null;
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
        if (page is WinoPage.ManageAccountsPage or WinoPage.AccountManagementPage)
        {
            return NavigateInternal(WinoPage.SettingsPage, WinoPage.ManageAccountsPage, frame, transition);
        }

        var pageType = GetPageType(page);
        if (pageType == null) return false;

        var currentApplicationMode = _statePersistanceService.ApplicationMode;

        if (!IsPageAllowedInMode(currentApplicationMode, page))
        {
            return false;
        }

        _statePersistanceService.IsReadingMail = _renderingPageTypes.Contains(page);
        _statePersistanceService.IsEventDetailsVisible = page == WinoPage.EventDetailsPage || page == WinoPage.CalendarEventComposePage;

        Frame? innerShellFrame = GetCoreFrameInternal(NavigationReferenceFrame.InnerShellFrame);
        if (innerShellFrame == null && frame == NavigationReferenceFrame.ShellFrame)
        {
            var requestedFrame = GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame, WinoWindowKind.Welcome);
            if (requestedFrame == null)
                return false;

            return requestedFrame.Navigate(pageType, parameter, GetNavigationTransitionInfo(transition));
        }

        if (innerShellFrame != null)
        {
            // Calendar navigations.
            if (currentApplicationMode == WinoApplicationMode.Calendar)
            {
                var currentFrameType = GetCurrentFrameType(innerShellFrame);

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

                return NavigateInnerShellFrame(innerShellFrame, pageType, parameter, transition);
            }
            else
            {
                // Mail navigations.
                var currentFrameType = GetCurrentFrameType(innerShellFrame);
                bool isMailListingPageActive = currentFrameType != null && currentFrameType == typeof(MailListPage);

                // Active page is mail list page and we are refreshing the folder.
                if (isMailListingPageActive && currentFrameType == pageType && parameter is NavigateMailFolderEventArgs folderNavigationArgs)
                {
                    // No need for new navigation, just refresh the folder.
                    WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folderNavigationArgs.BaseFolderMenuItem, folderNavigationArgs.FolderInitLoadAwaitTask));
                    WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

                    return true;
                }

                // This page must be opened in the Frame placed in MailListingPage.
                if (isMailListingPageActive && frame == NavigationReferenceFrame.RenderingFrame)
                {
                    var listingFrame = GetCoreFrameInternal(NavigationReferenceFrame.RenderingFrame);
                    if (listingFrame == null) return false;

                    var transitionInfo = GetNavigationTransitionInfo(transition);

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
                    return NavigateInnerShellFrame(innerShellFrame, pageType, parameter, transition);
                }
            }
        }

        return false;
    }

    private static bool IsMailOnlyPage(WinoPage page)
        => MailOnlyPages.Contains(page);

    private static bool IsCalendarOnlyPage(WinoPage page)
        => CalendarOnlyPages.Contains(page);

    private static bool IsContactsOnlyPage(WinoPage page)
        => ContactsOnlyPages.Contains(page);

    private static bool IsPageAllowedInMode(WinoApplicationMode mode, WinoPage page)
        => mode switch
        {
            WinoApplicationMode.Mail => !IsCalendarOnlyPage(page) && !IsContactsOnlyPage(page),
            WinoApplicationMode.Calendar => !IsMailOnlyPage(page) && !IsContactsOnlyPage(page),
            WinoApplicationMode.Contacts => !IsMailOnlyPage(page) && !IsCalendarOnlyPage(page),
            _ => true
        };

    private static string GetApplicationModeTitle(WinoApplicationMode mode)
        => mode switch
        {
            WinoApplicationMode.Calendar => "Wino Calendar",
            WinoApplicationMode.Contacts => "Wino Contacts",
            _ => "Wino Mail"
        };

    private static NavigationTransitionInfo GetApplicationModeTransitionInfo(WinoApplicationMode currentMode, WinoApplicationMode targetMode)
    {
        var slideEffect = IsNextMode(currentMode, targetMode)
            ? SlideNavigationTransitionEffect.FromRight
            : SlideNavigationTransitionEffect.FromLeft;

        return new SlideNavigationTransitionInfo
        {
            Effect = slideEffect
        };
    }

    private static bool IsNextMode(WinoApplicationMode currentMode, WinoApplicationMode targetMode)
        => currentMode switch
        {
            WinoApplicationMode.Mail => targetMode == WinoApplicationMode.Calendar,
            WinoApplicationMode.Calendar => targetMode == WinoApplicationMode.Contacts,
            WinoApplicationMode.Contacts => targetMode == WinoApplicationMode.Mail,
            _ => false
        };

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

    private bool NavigateInnerShellFrame(Frame frame, Type pageType, object? parameter, NavigationTransitionType transition)
    {
        var transitionInfo = ConsumeInnerShellTransitionOrDefault(transition);
        return frame.Navigate(pageType, parameter, transitionInfo);
    }

    private NavigationTransitionInfo ConsumeInnerShellTransitionOrDefault(NavigationTransitionType transition)
    {
        if (_pendingInnerShellTransition != null)
        {
            var transitionInfo = _pendingInnerShellTransition;
            _pendingInnerShellTransition = null;
            return transitionInfo;
        }

        return GetNavigationTransitionInfo(transition);
    }

    public void GoBack(Core.Domain.Enums.NavigationTransitionEffect slideEffect = Core.Domain.Enums.NavigationTransitionEffect.FromRight)
        => ExecuteOnNavigationThread(() => GoBackInternal(slideEffect));

    private void GoBackInternal(Core.Domain.Enums.NavigationTransitionEffect slideEffect = Core.Domain.Enums.NavigationTransitionEffect.FromRight)
    {
        if (_statePersistanceService.IsSettingsNavigating)
        {
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
