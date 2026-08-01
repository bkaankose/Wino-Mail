using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class EditorPage : Page
{
    public EditorPage()
    {
        InitializeComponent();
    }

    private async void ComposeEditor_Loaded(object sender, RoutedEventArgs e)
    {
        await ComposeEditor.SetHtmlAsync("<p>Hi team,</p><p>Here is the latest design review summary. Please add comments before Friday.</p><p>Thanks,<br/>Avery</p>");
    }
}
