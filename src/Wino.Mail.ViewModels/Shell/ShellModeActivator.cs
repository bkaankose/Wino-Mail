#nullable enable

using System;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;

namespace Wino.Shell.ViewModels;

/// <summary>
/// The Windows WinoAppShell.ActivateMode sequence for native shells (Mac, iPhone, iPad and Android), over the shared
/// <see cref="WinoAppShellViewModel"/>: record the mode, release the outgoing provider's menu, clear the content host,
/// let the new provider navigate its root page (Mail awaits its first folder route) and then publish its menu. A mode
/// whose root page is not built yet leaves the content empty rather than failing. The shell supplies how its content is
/// cleared and how errors are reported.
/// </summary>
public sealed class ShellModeActivator(WinoAppShellViewModel viewModel, IStatePersistanceService state, Action<Exception> reportError)
{
    /// <summary>The mode on screen, or null before the first activation.</summary>
    public WinoApplicationMode? ActiveMode { get; private set; }

    /// <summary>The mode to start in: the persisted mode unless it is Settings or unavailable.</summary>
    public static WinoApplicationMode StartMode(IStatePersistanceService state, Func<WinoApplicationMode, bool>? isAvailable = null)
    {
        var start = state.ApplicationMode;
        if (start == WinoApplicationMode.Settings || isAvailable?.Invoke(start) == false) start = WinoApplicationMode.Mail;
        return start;
    }

    /// <summary>The mode's title in the shell's own language (window and app bar titles).</summary>
    public static string ModeTitle(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => Translator.KeyboardShortcuts_ModeCalendar,
        WinoApplicationMode.Contacts => Translator.ContactsPage_Title,
        WinoApplicationMode.Tasks => Translator.ToDoPage_Title,
        _ => Translator.KeyboardShortcuts_ModeMail
    };

    /// <summary>
    /// Switches to <paramref name="mode"/>. Settings is never a mode and is ignored.
    /// </summary>
    /// <param name="isShowingMode">
    /// True when the shell still shows the active mode's content; re-activating it then only passes a new parameter to
    /// the provider. Null treats the active mode as shown.
    /// </param>
    /// <param name="clearContent">Releases and removes the shell's content page.</param>
    /// <param name="modeTitle">The title the shell records for the mode; null uses <see cref="ModeTitle"/>.</param>
    /// <returns>True when the mode was activated and its menu published.</returns>
    public async Task<bool> ActivateAsync(WinoApplicationMode mode, ShellModeActivationContext? context, Func<Task> clearContent,
        IDispatcher dispatcher, Func<bool>? isShowingMode = null, Func<WinoApplicationMode, string>? modeTitle = null)
    {
        context ??= new ShellModeActivationContext { IsInitialActivation = false };
        if (mode == WinoApplicationMode.Settings) return false;
        IShellMenuProvider provider;
        try { provider = viewModel.GetProvider(mode); }
        catch (Exception exception) { reportError(exception); return false; }

        if (ActiveMode == mode && (isShowingMode?.Invoke() ?? true))
        {
            if (context.Parameter is not null) provider.ActivateShellMenu(context);
            return false;
        }

        state.ApplicationMode = mode;
        state.AppModeTitle = (modeTitle ?? ModeTitle)(mode);
        state.CoreWindowTitle = string.Empty;
        viewModel.SetShellMenu(null);
        if (ActiveMode is { } previous && viewModel.TryGetProvider(previous, out var outgoing) && outgoing is not null)
        {
            try { outgoing.ReleaseShellMenu(); }
            catch (Exception exception) { reportError(exception); }
        }
        ActiveMode = mode;
        viewModel.SetCurrentMode(mode);
        await clearContent();
        if (!ReferenceEquals(provider.Dispatcher, dispatcher)) provider.Dispatcher = dispatcher;
        try
        {
            // Mail awaits its first folder route; the other modes navigate through the router themselves.
            if (provider is MailAppShellViewModel mail) await mail.InitializeNavigationAsync(NavigationMode.New, context);
            else provider.ActivateShellMenu(context);
        }
        catch (Exception exception) { reportError(exception); }
        if (ActiveMode != mode) return false;
        viewModel.SetShellMenu(provider);
        return true;
    }
}
