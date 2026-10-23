using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIEx;
using MailCal.Mail.WinUI.Models;

namespace MailCal.Mail.WinUI.Interfaces;

public interface IMailCalWindowManager
{
    event EventHandler<WindowEx?> ActiveWindowChanged;
    event EventHandler<WindowEx> WindowRemoved;

    WindowEx? ActiveWindow { get; }
    WindowEx CreateWindow(MailCalWindowKind kind, Func<WindowEx> factory, string? name = null);

    /// <summary>
    /// Brings the window of the given kind to the front, creating it through <paramref name="factory"/>
    /// when it does not exist yet. A new window gets the active theme applied before it is shown,
    /// so it never flashes with the wrong resources or backdrop.
    /// </summary>
    Task<WindowEx> ShowThemedWindowAsync(MailCalWindowKind kind, Func<WindowEx> factory, string? name = null);
    WindowEx? GetWindow(MailCalWindowKind kind, string? name = null);
    WindowEx? GetWindow(string name);
    IReadOnlyList<WindowEx> GetWindows();
    void ActivateWindow(WindowEx window);
    bool ActivateWindow(MailCalWindowKind kind, string? name = null);
    void HideWindow(WindowEx window);
    bool HideWindow(MailCalWindowKind kind, string? name = null);
    void CloseAllWindows();
}
