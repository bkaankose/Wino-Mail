using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Entities.Mail;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class EmailTemplatesPage : EmailTemplatesPageAbstract
{
    public EmailTemplatesPage()
    {
        InitializeComponent();
    }

    protected async override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void NewTemplateClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.CreateTemplate();
    }

    private void TemplateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is EmailTemplate template)
        {
            ViewModel.OpenTemplate(template);
        }
    }
}
