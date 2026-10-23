using Microsoft.UI.Xaml;
using MailCal.Mail.ViewModels.Data;
using MailCal.Views.Abstract;
using WinRT;

namespace MailCal.Views;

public sealed partial class FolderCustomizationPage : FolderCustomizationPageAbstract
{
    public FolderCustomizationPage()
    {
        InitializeComponent();
    }

    private async void ListView_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        // ListView.CanReorderItems automatically mutates the backing
        // ObservableCollection; persist the new order here.
        await ViewModel.PersistLayoutAsync();
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private async void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FolderCustomizationItemViewModel item)
        {
            await ViewModel.TogglePinAsync(item);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private async void HideToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FolderCustomizationItemViewModel item)
        {
            await ViewModel.ToggleHiddenAsync(item);
        }
    }
}
