using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Playground.ViewModels;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class AccountIconPage : Page
{
    public AccountIconPageViewModel ViewModel { get; } = new();

    public WinoAccountIconSource InfoBarAccountIconSource { get; } = new();

    public AccountIconPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        UpdateInfoBarIconSource();
    }

    private async void BrowseProfilePictureClick(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        var picker = new FileOpenPicker(element.XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");

        var result = await picker.PickSingleFileAsync();
        if (result is not null)
        {
            ViewModel.SetProfilePicture(result.Path);
        }
    }

    private void ClearProfilePictureClick(object sender, RoutedEventArgs args) =>
        ViewModel.SetProfilePicture(null);

    private void ThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        RequestedTheme = ((sender as ComboBox)?.SelectedItem as ComboBoxItem)?.Tag switch
        {
            "Light" => ElementTheme.Light,
            "Default" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
    }

    private void ViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AccountIconPageViewModel.Account) or
            nameof(AccountIconPageViewModel.IsProfilePictureEnabled))
        {
            UpdateInfoBarIconSource();
        }
    }

    private void UpdateInfoBarIconSource()
    {
        InfoBarAccountIconSource.Account = ViewModel.Account;
        InfoBarAccountIconSource.IsProfilePictureEnabled = ViewModel.IsProfilePictureEnabled;
        InfoBarAccountIconSource.IconSize = 20;
    }

    private void PageUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        InfoBarAccountIconSource.Dispose();
    }
}
