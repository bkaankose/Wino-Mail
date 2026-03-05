using System;
using Microsoft.UI.Xaml.Controls;
using WinUIEx;
using Wino.Mail.WinUI.Models;

namespace Wino.Mail.WinUI.Interfaces;

public interface IWinoWindowManager
{
    event EventHandler<WindowEx?> ActiveWindowChanged;
    event EventHandler<WindowEx> WindowRemoved;

    WindowEx? ActiveWindow { get; }
    WindowEx CreateWindow(WinoWindowKind kind, Func<WindowEx> factory, string? name = null);
    WindowEx? GetWindow(WinoWindowKind kind, string? name = null);
    WindowEx? GetWindow(string name);
    void ActivateWindow(WindowEx window);
    bool ActivateWindow(WinoWindowKind kind, string? name = null);
    void HideWindow(WindowEx window);
    bool HideWindow(WinoWindowKind kind, string? name = null);
    void SetPrimaryNavigationFrame(WinoWindowKind kind, Frame frame, string? name = null);
    Frame? GetPrimaryNavigationFrame(WinoWindowKind kind, string? name = null);
    void CloseAllWindows();
}
