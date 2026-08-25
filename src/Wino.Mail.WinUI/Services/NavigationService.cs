#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Navigation;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Views;
using Wino.Views.Mail;

namespace Wino.Services;

public class NavigationService : NavigationServiceBase, INavigationService
{
    private readonly IStatePersistanceService _statePersistanceService;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoWindowManager _windowManager;
    private readonly NavigationReentryRuleSet _reentryRules;

    private NavigationTransitionInfo? _pendingInnerShellTransition;
    private NavigationResult? _pendingNavigationResult;

    public NavigationService(IStatePersistanceService statePersistanceService,
                             IDispatcher dispatcher,
                             IWinoWindowManager windowManager,
                             IEnumerable<INavigationReentryRule> reentryRules)
    {
        _statePersistanceService = statePersistanceService;
        _dispatcher = dispatcher;
        _windowManager = windowManager;
        _reentryRules = new NavigationReentryRuleSet(reentryRules);
    }

    public Type? GetPageType(WinoPage winoPage) => NavigationRouteTable.GetPageType(winoPage);

    #region Thread marshalling

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

    #endregion

    #region Frame resolution

    private Frame? GetCoreFrameInternal(NavigationReferenceFrame frameType, WinoWindowKind? requestedWindowKind = null)
    {
        if (frameType == NavigationReferenceFrame.ShellFrame)
        {
            if (requestedWindowKind.HasValue)
                return GetWindowShellFrame(requestedWindowKind.Value);

            return GetActiveWindowShellFrame()
                ?? GetWindowShellFrame(WinoWindowKind.Shell)
                ?? GetWindowShellFrame(WinoWindowKind.Welcome);
        }

        var frame = GetFrameFromShellContent(GetActiveWindowShellFrame(), frameType);
        if (frame != null)
            return frame;

        return GetFrameFromShellContent(GetWindowShellFrame(WinoWindowKind.Shell), frameType);
    }

    private Frame? GetActiveWindowShellFrame()
        => (_windowManager.ActiveWindow as IWinoFrameProvider)?.GetFrame(NavigationReferenceFrame.ShellFrame);

    private Frame? GetWindowShellFrame(WinoWindowKind windowKind)
        => (_windowManager.GetWindow(windowKind) as IWinoFrameProvider)?.GetFrame(NavigationReferenceFrame.ShellFrame);

    private static Frame? GetFrameFromShellContent(Frame? shellFrame, NavigationReferenceFrame frameType)
        => (shellFrame?.Content as IWinoFrameProvider)?.GetFrame(frameType);

    private IShellMenuSink? GetShellMenuSink()
        => GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame, WinoWindowKind.Shell)?.Content as IShellMenuSink;

    #endregion

    #region Application mode

    public bool ChangeApplicationMode(WinoApplicationMode mode)
        => ExecuteOnNavigationThread(() => ChangeApplicationModeInternal(mode));

    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
        => ExecuteOnNavigationThread(() => ChangeApplicationModeInternal(mode, activationContext));

    public bool ParkShell()
        => ExecuteOnNavigationThread(ParkShellInternal);

    public bool RestoreShell(WinoApplicationMode mode)
        => ExecuteOnNavigationThread(() => RestoreShellInternal(mode));

    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext)
        => ExecuteOnNavigationThread(() => RestoreShellInternal(mode, activationContext));

    private bool ParkShellInternal()
    {
        var coreFrame = GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame, WinoWindowKind.Shell);

        if (coreFrame == null)
            return false;

        if (coreFrame.Content is IdlePage)
            return true;

        _pendingInnerShellTransition = null;
        _pendingNavigationResult = null;
        _statePersistanceService.IsReadingMail = false;
        _statePersistanceService.IsEventDetailsVisible = false;
        _statePersistanceService.CoreWindowTitle = string.Empty;

        if (coreFrame.Content is WinoAppShell shellPage)
        {
            shellPage.PrepareForWindowClose();
        }

        WindowCleanupHelper.ClearNavigationStack(coreFrame);

        return coreFrame.Navigate(typeof(IdlePage), null, new SuppressNavigationTransitionInfo());
    }

    private bool RestoreShellInternal(WinoApplicationMode mode, ShellModeActivationContext? activationContext = null)
        => ChangeApplicationModeInternal(mode, activationContext, WinoWindowKind.Shell);

    private bool ChangeApplicationModeInternal(WinoApplicationMode mode,
                                               ShellModeActivationContext? activationContext = null,
                                               WinoWindowKind? requestedWindowKind = null)
    {
        var coreFrame = GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame, requestedWindowKind);

        if (coreFrame == null) return false;

        var currentMode = _statePersistanceService.ApplicationMode;
        var isInitialShellNavigation = coreFrame.Content is not IShellHost;

        _statePersistanceService.ApplicationMode = mode;
        _statePersistanceService.AppModeTitle = GetApplicationModeTitle(mode);

        // Re-activating the mode already on screen only forwards the parameter.
        if (coreFrame.Content is IShellHost activeShell && activeShell.HasShellContent && currentMode == mode)
        {
            if (activationContext?.Parameter != null)
            {
                activeShell.ActivateMode(mode, new ShellModeActivationContext
                {
                    IsInitialActivation = false,
                    SuppressStartupFlows = activationContext.SuppressStartupFlows,
                    Parameter = activationContext.Parameter
                });
            }

            return true;
        }

        _pendingInnerShellTransition = isInitialShellNavigation
            ? null
            : GetApplicationModeTransitionInfo(currentMode, mode);

        // The subtitle belongs to whatever the previous mode was showing. Modes set their
        // own once their content lands.
        _statePersistanceService.CoreWindowTitle = string.Empty;

        // Release the outgoing menu before anything else so the navigation view drops its
        // item containers while the collections behind them are still alive.
        ReleaseCurrentShellMenu();

        if (coreFrame.Content is not IShellHost)
        {
            WindowCleanupHelper.ClearNavigationStack(coreFrame);
            coreFrame.Navigate(typeof(WinoAppShell), null, new SuppressNavigationTransitionInfo());
        }
        else
        {
            // Tear down the previous mode's pages. Cached mode roots are evicted here so a
            // second visit rebuilds cleanly instead of resurrecting stale state.
            WindowCleanupHelper.CleanupFrame(GetCoreFrameInternal(NavigationReferenceFrame.InnerShellFrame));
        }

        if (coreFrame.Content is IShellHost shell)
        {
            shell.ActivateMode(mode, new ShellModeActivationContext
            {
                IsInitialActivation = isInitialShellNavigation,
                SuppressStartupFlows = activationContext?.SuppressStartupFlows ?? false,
                Parameter = activationContext?.Parameter
            });

            return true;
        }

        _pendingInnerShellTransition = null;
        return true;
    }

    private void ReleaseCurrentShellMenu()
    {
        var sink = GetShellMenuSink();
        sink?.SetShellMenu(null);
    }

    private static string GetApplicationModeTitle(WinoApplicationMode mode)
        => mode switch
        {
            WinoApplicationMode.Calendar => "Wino Calendar",
            WinoApplicationMode.Contacts => "Wino People",
            WinoApplicationMode.Tasks => "Wino To Do",
            WinoApplicationMode.Settings => "Wino Settings",
            _ => "Wino Mail"
        };

    private static NavigationTransitionInfo GetApplicationModeTransitionInfo(WinoApplicationMode currentMode, WinoApplicationMode targetMode)
        => new SlideNavigationTransitionInfo
        {
            Effect = IsNextMode(currentMode, targetMode)
                ? SlideNavigationTransitionEffect.FromRight
                : SlideNavigationTransitionEffect.FromLeft
        };

    private static bool IsNextMode(WinoApplicationMode currentMode, WinoApplicationMode targetMode)
        => currentMode switch
        {
            WinoApplicationMode.Mail => targetMode == WinoApplicationMode.Calendar,
            WinoApplicationMode.Calendar => targetMode == WinoApplicationMode.Contacts,
            WinoApplicationMode.Contacts => targetMode == WinoApplicationMode.Tasks,
            WinoApplicationMode.Tasks => targetMode == WinoApplicationMode.Settings,
            WinoApplicationMode.Settings => targetMode == WinoApplicationMode.Mail,
            _ => false
        };

    #endregion

    #region Forward navigation

    public bool Navigate(WinoPage page,
                         object? parameter = null,
                         NavigationReferenceFrame? frame = null,
                         NavigationTransitionType transition = NavigationTransitionType.None)
        => ExecuteOnNavigationThread(() => NavigateInternal(page, parameter, frame, transition));

    private bool NavigateInternal(WinoPage page,
                                  object? parameter,
                                  NavigationReferenceFrame? requestedFrame,
                                  NavigationTransitionType transition)
    {
        // Settings pages are reached by activating Settings mode, never by navigating the
        // inner frame straight to them; the settings page owns its own breadcrumb frame.
        if (TryGetSettingsActivationTarget(page, parameter, out var settingsTarget))
        {
            if (_statePersistanceService.ApplicationMode != WinoApplicationMode.Settings)
            {
                return ChangeApplicationModeInternal(WinoApplicationMode.Settings, new ShellModeActivationContext
                {
                    Parameter = settingsTarget
                });
            }

            page = WinoPage.SettingsPage;
            parameter = settingsTarget;
        }

        var route = NavigationRouteTable.Find(page);
        if (route == null) return false;

        var currentMode = _statePersistanceService.ApplicationMode;
        if (!route.IsAllowedIn(currentMode)) return false;

        var targetFrameType = requestedFrame ?? route.Frame;
        var innerShellFrame = GetCoreFrameInternal(NavigationReferenceFrame.InnerShellFrame);

        // The account setup wizard takes over the inner shell when one exists, and owns the
        // welcome window's root frame when it does not.
        if (route.Kind == RouteKind.Standalone || targetFrameType == NavigationReferenceFrame.ShellFrame)
        {
            if (innerShellFrame == null)
                return NavigateStandalone(route, parameter, transition);

            targetFrameType = NavigationReferenceFrame.InnerShellFrame;
        }

        var frame = targetFrameType == NavigationReferenceFrame.InnerShellFrame
            ? innerShellFrame
            : GetCoreFrameInternal(targetFrameType);

        if (frame == null) return false;

        var context = new NavigationContext
        {
            Page = page,
            Route = route,
            Frame = frame,
            Mode = currentMode,
            Parameter = parameter
        };

        var decision = _reentryRules.Evaluate(context);

        switch (decision.Action)
        {
            case ReentryAction.Suppress:
                return true;

            case ReentryAction.HandleInPlace:
                _ = decision.Callback!();
                return true;

            case ReentryAction.ReuseBackStackEntry:
                frame.GoBack();
                SyncModeStateFromContent(frame);
                PublishShellMenuForContent(frame);
                _pendingNavigationResult = null;
                _ = decision.Callback!();
                return true;
        }

        return NavigateFrame(frame, route, parameter, transition, targetFrameType);
    }

    private bool NavigateStandalone(NavigationRoute route, object? parameter, NavigationTransitionType transition)
    {
        var shellFrame = GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame, WinoWindowKind.Welcome)
                         ?? GetCoreFrameInternal(NavigationReferenceFrame.ShellFrame);

        return shellFrame?.Navigate(route.PageType, parameter, GetNavigationTransitionInfo(transition)) == true;
    }

    private bool NavigateFrame(Frame frame,
                               NavigationRoute route,
                               object? parameter,
                               NavigationTransitionType transition,
                               NavigationReferenceFrame targetFrameType)
    {
        var isInnerShellNavigation = targetFrameType == NavigationReferenceFrame.InnerShellFrame;

        if (isInnerShellNavigation)
        {
            PruneInnerShellBackStackForMode(frame, _statePersistanceService.ApplicationMode);
        }

        var transitionInfo = isInnerShellNavigation
            ? ConsumeInnerShellTransitionOrDefault(transition)
            : GetNavigationTransitionInfo(transition);

        if (!frame.Navigate(route.PageType, parameter, transitionInfo))
            return false;

        if (isInnerShellNavigation)
        {
            // Only detail pages contribute to the inner back stack. Anything else replaces
            // the mode's content, so the stack is reset behind it.
            if (route.Kind != RouteKind.Detail)
            {
                WindowCleanupHelper.ClearNavigationStack(frame);
            }

            PublishShellMenuForContent(frame);
        }

        SyncModeStateFromContent(frame, route);

        return true;
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

    private static bool TryGetSettingsActivationTarget(WinoPage page, object? parameter, out object settingsTarget)
    {
        settingsTarget = WinoPage.SettingOptionsPage;

        if (page == WinoPage.SettingsPage)
        {
            settingsTarget = parameter switch
            {
                SettingsPageActivationContext activationContext => activationContext,
                WinoPage targetPage => targetPage,
                _ => WinoPage.SettingOptionsPage
            };
            return true;
        }

        var route = NavigationRouteTable.Find(page);

        if (route is not { Kind: RouteKind.Hosted, Mode: WinoApplicationMode.Settings })
            return false;

        settingsTarget = SettingsNavigationInfoProvider.GetRootPage(page);
        return true;
    }

    #endregion

    #region Back navigation

    public bool CanGoBack()
        => ExecuteOnNavigationThread(CanGoBackInternal);

    public void SetNavigationResult(NavigationResult result) => _pendingNavigationResult = result;

    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
        => _ = GoBackAsync(slideEffect);

    public Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
    {
        if (IsOnNavigationThread())
            return GoBackInternalAsync(slideEffect);

        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = _dispatcher.ExecuteOnUIThread(async () =>
        {
            try
            {
                completionSource.TrySetResult(await GoBackInternalAsync(slideEffect));
            }
            catch (Exception exception)
            {
                completionSource.TrySetException(exception);
            }
        });

        return completionSource.Task;
    }

    private bool CanGoBackInternal()
    {
        var innerShellFrame = GetCoreFrameInternal(NavigationReferenceFrame.InnerShellFrame);

        if (innerShellFrame == null)
            return false;

        if (innerShellFrame.Content is IInnerNavigationHost innerHost && innerHost.CanNavigateBack)
            return true;

        return HasModeScopedBackStack(innerShellFrame, _statePersistanceService.ApplicationMode);
    }

    private async Task<bool> GoBackInternalAsync(NavigationTransitionEffect slideEffect)
    {
        var innerShellFrame = GetCoreFrameInternal(NavigationReferenceFrame.InnerShellFrame);

        if (innerShellFrame == null)
            return false;

        // The page on screen may refuse to leave, for example while it holds unsaved edits.
        if (innerShellFrame.Content is BasePage currentPage &&
            currentPage.AssociatedViewModel is IConfirmBackNavigation confirmBackNavigation &&
            !await confirmBackNavigation.CanNavigateBackAsync())
        {
            _pendingNavigationResult = null;
            return false;
        }

        // Pages that navigate inside themselves consume the request first.
        if (innerShellFrame.Content is IInnerNavigationHost innerHost &&
            innerHost.CanNavigateBack &&
            await innerHost.NavigateBackAsync(slideEffect))
        {
            return true;
        }

        PruneInnerShellBackStackForMode(innerShellFrame, _statePersistanceService.ApplicationMode);

        if (innerShellFrame.CanGoBack)
        {
            // Captured before the pop: this is the parameter the destination is restored with.
            var destinationParameter = innerShellFrame.BackStack[^1].Parameter;

            innerShellFrame.GoBack();
            CompleteBackNavigation(innerShellFrame, destinationParameter);
            return true;
        }

        return TryReturnToModeRoot(innerShellFrame);
    }

    /// <summary>
    /// Safety net for a mode whose detail page survived a back stack prune: put the mode
    /// root back on screen rather than leaving a detail page with nowhere to return to.
    /// </summary>
    private bool TryReturnToModeRoot(Frame innerShellFrame)
    {
        var currentRoute = NavigationRouteTable.Find(innerShellFrame.Content?.GetType());

        if (currentRoute == null || currentRoute.Kind == RouteKind.ModeRoot)
            return false;

        var modeRoot = GetModeRoot(_statePersistanceService.ApplicationMode);

        if (modeRoot == null)
            return false;

        var resetParameter = GetModeRootResetParameter(modeRoot);

        if (!innerShellFrame.Navigate(modeRoot.PageType, resetParameter, new SuppressNavigationTransitionInfo()))
            return false;

        WindowCleanupHelper.ClearNavigationStack(innerShellFrame);
        CompleteBackNavigation(innerShellFrame, resetParameter);
        return true;
    }

    private void CompleteBackNavigation(Frame innerShellFrame, object? destinationParameter)
    {
        SyncModeStateFromContent(innerShellFrame);
        PublishShellMenuForContent(innerShellFrame);
        DeliverPendingNavigationResult(innerShellFrame, destinationParameter);
    }

    private void DeliverPendingNavigationResult(Frame innerShellFrame, object? destinationParameter)
    {
        var result = _pendingNavigationResult;
        _pendingNavigationResult = null;

        if (innerShellFrame.Content is BasePage page &&
            page.AssociatedViewModel is IBackNavigationAware backNavigationAware)
        {
            backNavigationAware.OnNavigatedBack(destinationParameter, result);
        }
    }

    private static NavigationRoute? GetModeRoot(WinoApplicationMode mode)
    {
        foreach (var route in NavigationRouteTable.All)
        {
            if (route.Kind == RouteKind.ModeRoot && route.Mode == mode)
                return route;
        }

        return null;
    }

    private static object? GetModeRootResetParameter(NavigationRoute modeRoot)
        => modeRoot.Page == WinoPage.CalendarPage
            ? new Core.Domain.Models.Calendar.CalendarPageNavigationArgs { RequestDefaultNavigation = true }
            : null;

    private static bool HasModeScopedBackStack(Frame innerShellFrame, WinoApplicationMode mode)
    {
        for (int i = innerShellFrame.BackStack.Count - 1; i >= 0; i--)
        {
            if (NavigationRouteTable.IsAllowedIn(mode, innerShellFrame.BackStack[i].SourcePageType))
                return true;
        }

        return false;
    }

    private static void PruneInnerShellBackStackForMode(Frame frame, WinoApplicationMode mode)
    {
        for (int i = frame.BackStack.Count - 1; i >= 0; i--)
        {
            if (!NavigationRouteTable.IsAllowedIn(mode, frame.BackStack[i].SourcePageType))
            {
                frame.BackStack.RemoveAt(i);
            }
        }
    }

    #endregion

    #region Shell state

    private void PublishShellMenuForContent(Frame innerShellFrame)
    {
        // Detail pages keep the menu their mode root published, so drilling into one does
        // not blank the navigation pane.
        if (innerShellFrame.Content is not BasePage page ||
            page.AssociatedViewModel is not IShellMenuOwner menuOwner)
        {
            return;
        }

        GetShellMenuSink()?.SetShellMenu(menuOwner.ShellMenuProvider);
    }

    private void SyncModeStateFromContent(Frame frame, NavigationRoute? route = null)
    {
        route ??= NavigationRouteTable.Find(frame.Content?.GetType());

        if (route == null)
            return;

        // Both flags are derived from the route rather than from a hard-coded page list, so
        // a new reading pane or calendar detail page picks them up for free.
        _statePersistanceService.IsReadingMail = route is { Kind: RouteKind.Rendering, Mode: WinoApplicationMode.Mail };
        _statePersistanceService.IsEventDetailsVisible = route is { Kind: RouteKind.Detail, Mode: WinoApplicationMode.Calendar };
    }

    #endregion
}
