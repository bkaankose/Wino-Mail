using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class CreateEmailTemplatePage : CreateEmailTemplatePageAbstract
{
    public CreateEmailTemplatePage()
    {
        InitializeComponent();
    }

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var htmlContent = await ViewModel.LoadAsync(e.Parameter);
        await WebViewEditor.RenderHtmlAsync(htmlContent);

        if (!ViewModel.IsExistingTemplate)
        {
            TemplateNameTextBox.Focus(FocusState.Programmatic);
        }
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        WebViewEditor.Dispose();
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveAsync(await WebViewEditor.GetHtmlBodyAsync() ?? string.Empty);
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        await ViewModel.DeleteAsync();
    }
}
