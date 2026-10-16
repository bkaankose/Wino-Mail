using Microsoft.UI.Xaml;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;
using WinRT;

namespace Wino.Views;

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
