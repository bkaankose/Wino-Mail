using CommunityToolkit.WinUI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Wino.Mail.Controls.HotKeyInput;

public sealed partial class WinoHotKeyInput : Button
{
    private VirtualKey _capturedKey;
    private VirtualKeyModifiers _capturedModifiers;

    [GeneratedDependencyProperty(DefaultValue = VirtualKey.Space)]
    public partial VirtualKey Key { get; set; }

    [GeneratedDependencyProperty(DefaultValue = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)]
    public partial VirtualKeyModifiers Modifiers { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Press shortcut")]
    public partial string NormalPrompt { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Press keys now")]
    public partial string ListeningPrompt { get; set; }

    public bool IsCapturing { get; private set; }

    public event EventHandler<HotKeyCommittedEventArgs>? HotKeyCommitted;

    /// <summary>
    /// Raised when the control starts listening for a new shortcut. Hosts that register the
    /// current shortcut globally should suspend it, or the system consumes the keystrokes.
    /// </summary>
    public event EventHandler? CaptureStarted;

    /// <summary>
    /// Raised when listening ends without a committed shortcut (Escape, focus loss or unload).
    /// </summary>
    public event EventHandler? CaptureCanceled;

    public WinoHotKeyInput()
    {
        DefaultStyleKey = typeof(Button);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Left;

        // Subscribe once for the control's lifetime. Subscribing in Loaded and removing in
        // Unloaded loses the handler when a host (SettingsExpander items, ItemsRepeater)
        // raises Loaded for the new placement before Unloaded for the old one.
        Click += OnControlClicked;
        Unloaded += OnUnloaded;
        UpdateDisplay();
    }

    partial void OnKeyChanged(VirtualKey newValue) => UpdateDisplay();

    partial void OnModifiersChanged(VirtualKeyModifiers newValue) => UpdateDisplay();

    partial void OnNormalPromptChanged(string newValue) => UpdateDisplay();

    partial void OnListeningPromptChanged(string newValue) => UpdateDisplay();

    private void OnUnloaded(object sender, RoutedEventArgs e) => CancelCapture();

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!IsCapturing)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            CancelCapture();
            e.Handled = true;
            return;
        }

        _capturedModifiers = GetCurrentModifiers();
        if (!IsModifier(e.Key))
        {
            _capturedKey = e.Key;
        }

        UpdateDisplay();
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (!IsCapturing)
        {
            base.OnKeyUp(e);
            return;
        }

        if (_capturedKey != VirtualKey.None && e.Key == _capturedKey)
        {
            var args = new HotKeyCommittedEventArgs(_capturedKey, _capturedModifiers);
            IsCapturing = false;
            _capturedKey = VirtualKey.None;
            _capturedModifiers = VirtualKeyModifiers.None;
            UpdateDisplay();
            HotKeyCommitted?.Invoke(this, args);
        }

        e.Handled = true;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        CancelCapture();
        base.OnLostFocus(e);
    }

    public void CancelCapture()
    {
        if (!IsCapturing)
            return;

        IsCapturing = false;
        _capturedKey = VirtualKey.None;
        _capturedModifiers = VirtualKeyModifiers.None;
        UpdateDisplay();
        CaptureCanceled?.Invoke(this, EventArgs.Empty);
    }

    public static string Format(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(VirtualKeyModifiers.Menu))
            parts.Add("Alt");
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(VirtualKeyModifiers.Windows))
            parts.Add("Win");
        if (key != VirtualKey.None)
            parts.Add(key.ToString());

        return string.Join("+", parts);
    }

    private void BeginCapture()
    {
        if (IsCapturing)
            return;

        IsCapturing = true;
        _capturedKey = VirtualKey.None;
        _capturedModifiers = VirtualKeyModifiers.None;
        UpdateDisplay();

        if (FocusState == FocusState.Unfocused)
            Focus(FocusState.Programmatic);

        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnControlClicked(object sender, RoutedEventArgs e) => BeginCapture();

    private void UpdateDisplay()
    {
        var display = IsCapturing
            ? Format(_capturedKey, _capturedModifiers)
            : Format(Key, Modifiers);

        Content = string.IsNullOrWhiteSpace(display)
            ? IsCapturing ? ListeningPrompt : NormalPrompt
            : display;
        AutomationProperties.SetHelpText(this, IsCapturing ? ListeningPrompt : NormalPrompt);
    }

    private static VirtualKeyModifiers GetCurrentModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;

        if (IsDown(VirtualKey.Control))
            modifiers |= VirtualKeyModifiers.Control;
        if (IsDown(VirtualKey.Menu))
            modifiers |= VirtualKeyModifiers.Menu;
        if (IsDown(VirtualKey.Shift))
            modifiers |= VirtualKeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
            modifiers |= VirtualKeyModifiers.Windows;

        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsModifier(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;
}

public sealed class HotKeyCommittedEventArgs(VirtualKey key, VirtualKeyModifiers modifiers) : EventArgs
{
    public VirtualKey Key { get; } = key;
    public VirtualKeyModifiers Modifiers { get; } = modifiers;
}
