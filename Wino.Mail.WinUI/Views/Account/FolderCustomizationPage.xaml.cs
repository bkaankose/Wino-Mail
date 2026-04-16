using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class FolderCustomizationPage : FolderCustomizationPageAbstract
{
    public FolderCustomizationPage()
    {
        InitializeComponent();
    }

    private async void ListView_DropCompleted(UIElement sender, Microsoft.UI.Xaml.Controls.Primitives.DropCompletedEventArgs args)
    {
        // ListView.CanReorderItems automatically mutates the backing
        // ObservableCollection; persist the new order here.
        await ViewModel.PersistLayoutAsync();
    }

    private async void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FolderCustomizationItemViewModel item)
        {
            await ViewModel.TogglePinAsync(item);
        }
    }

    private async void HideToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is FolderCustomizationItemViewModel item)
        {
            await ViewModel.ToggleHiddenAsync(item);
        }
    }
}
