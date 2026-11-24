using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Editor;
using Wino.Renderer.WinUI;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// The mail editor element pages and dialogs place in XAML. It hosts the engine's <see cref="MailView"/> in composer
/// mode, which is built in code, and owns the <see cref="Session"/> that talks to it. The view scrolls its own
/// content and has no height of its own, so the host gives this element a size.
/// </summary>
public sealed partial class EngineMailEditorView : UserControl
{
    private readonly MailView _view = new();
    private bool _isApplyingSessionTheme;

    public EngineMailEditorView()
    {
        // Focus goes to the engine view inside; this element only places it.
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = _view;

        Session = new EngineMailEditorSession(_view);
        Session.StateChanged += Session_StateChanged;
        Session.LinkOpenRequested += Session_LinkOpenRequested;
        _view.FilesReceived += View_FilesReceived;
        _view.ContextMenuRequested += View_ContextMenuRequested;
        Loaded += EngineMailEditorView_Loaded;
    }

    [GeneratedDependencyProperty]
    public partial bool IsEditorDarkMode { get; set; }

    internal EngineMailEditorSession Session { get; }

    public bool HasKeyboardFocus => _view.FocusState != FocusState.Unfocused;

    /// <summary>Raised when the user opens a link with Ctrl+click or from the context menu. The host launches it.</summary>
    public event EventHandler<Uri>? LinkOpenRequested;

    /// <summary>Paths of files dropped or pasted into the editor that are not images; the host attaches them.</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesReceived;

    partial void OnIsEditorDarkModeChanged(bool newValue)
    {
        if (!_isApplyingSessionTheme)
            _ = ApplyThemeAsync(newValue);
    }

    private async Task ApplyThemeAsync(bool isDarkMode)
    {
        try
        {
            await Session.SetThemeAsync(isDarkMode);
        }
        catch (ObjectDisposedException)
        {
            // The owner closed the editor; a theme change has nothing left to repaint.
        }
    }

    private void Session_StateChanged(object? sender, EditorState state)
    {
        if (IsEditorDarkMode == state.IsDarkMode) return;

        _isApplyingSessionTheme = true;
        try
        {
            IsEditorDarkMode = state.IsDarkMode;
        }
        finally
        {
            _isApplyingSessionTheme = false;
        }
    }

    private void Session_LinkOpenRequested(object? sender, Uri uri) => LinkOpenRequested?.Invoke(this, uri);

    private void View_FilesReceived(object? sender, IReadOnlyList<string> files) => FilesReceived?.Invoke(this, files);

    // A UserControl has no automation peer, so the identity set in XAML moves to the view, which has one.
    private void EngineMailEditorView_Loaded(object sender, RoutedEventArgs e)
    {
        if (AutomationProperties.GetAutomationId(this) is { Length: > 0 } automationId)
            AutomationProperties.SetAutomationId(_view, automationId);

        if (AutomationProperties.GetName(this) is { Length: > 0 } name)
            AutomationProperties.SetName(_view, name);
    }
}
