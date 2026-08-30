using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using Wino.Editor;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class EditorPage : Page
{
    public EditorPage()
    {
        InitializeComponent();
        ComposeEditor.ApplicationShortcutRequested += ComposeEditor_ApplicationShortcutRequested;
    }

    private async void ComposeEditor_Loaded(object sender, RoutedEventArgs e)
    {
        await ComposeEditor.SetHtmlAsync("<p>Hi team,</p><p>Here is the latest design review summary. Please add comments before Friday.</p><p>Thanks,<br/>Avery</p>");
        await ComposeEditor.SetApplicationShortcutsAsync(
            new List<EditorApplicationShortcutGesture>
            {
                new("Enter", true, false, false)
            });
    }

    private void ComposeEditor_ApplicationShortcutRequested(object? sender, EditorApplicationShortcutGesture e)
        => ApplicationShortcutStatus.Text = $"Application shortcut forwarded: Ctrl+{e.Key}";
}
