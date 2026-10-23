using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Views.Abstract;
using WinRT;

namespace MailCal.Views.Settings;

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

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void TemplateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is EmailTemplate template)
        {
            ViewModel.OpenTemplate(template);
        }
    }
}
