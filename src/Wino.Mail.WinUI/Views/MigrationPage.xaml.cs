using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.WinUI.ViewModels;

namespace Wino.Mail.WinUI.Views;

public sealed partial class MigrationPage : Page
{
    public MigrationPageViewModel ViewModel { get; }

    public MigrationPage()
    {
        ViewModel = WinoApplication.Current.Services.GetRequiredService<MigrationPageViewModel>();
        InitializeComponent();
        ViewModel.InitializeDispatcher(DispatcherQueue);
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnPageLoaded;
        await ViewModel.InitializeAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => ViewModel.Dispose();

    private async void OnSkipMigrationClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Wino.Core.Domain.Translator.Migration_SkipTitle,
            Content = Wino.Core.Domain.Translator.Migration_SkipDescription,
            PrimaryButtonText = Wino.Core.Domain.Translator.Migration_SkipConfirm,
            CloseButtonText = Wino.Core.Domain.Translator.Migration_Cancel,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.StartFreshCommand.ExecuteAsync(null);
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
        => await (Application.Current as App)!.CompleteMigrationLaunchAsync();

    public async Task<bool> ConfirmExitAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Wino.Core.Domain.Translator.Migration_CloseTitle,
            Content = Wino.Core.Domain.Translator.Migration_CloseDescription,
            PrimaryButtonText = Wino.Core.Domain.Translator.Migration_Exit,
            CloseButtonText = Wino.Core.Domain.Translator.Migration_Cancel,
            DefaultButton = ContentDialogButton.Close
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
