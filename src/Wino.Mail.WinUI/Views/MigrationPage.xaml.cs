using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
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

    private void OnFeatureFlipViewSelectionChanged(object sender, SelectionChangedEventArgs e)
        => MigrationFeaturePager.SelectedPageIndex = MigrationFeatureFlipView.SelectedIndex;

    private void OnFeaturePagerSelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
        => MigrationFeatureFlipView.SelectedIndex = sender.SelectedPageIndex;

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

        if (await TryShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await ViewModel.StartFreshCommand.ExecuteAsync(null);
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app)
            return;

        try
        {
            await app.CompleteMigrationLaunchAsync();
        }
        catch (OperationCanceledException ex) when (app.IsExiting)
        {
            // Closing the application cancels the runtime start that Launch began.
            Log.Information(ex, "Migration launch was cancelled because the application is exiting.");
        }
        catch (Exception ex)
        {
            // This handler is async void: an escaping exception ends the process. The migration
            // window stays open, so the user can retry Launch or close the application.
            Log.Error(ex, "Launching Wino Mail after migration failed.");
        }
    }

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

        return await TryShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Shows a dialog on this page's XamlRoot. WinUI allows one open ContentDialog per XamlRoot; while
    /// another one is open (for example an account authentication prompt) ShowAsync throws a
    /// COMException. That is treated as a cancelled prompt.
    /// </summary>
    private static async Task<ContentDialogResult> TryShowDialogAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Migration prompt was not shown because another dialog is open.");
            return ContentDialogResult.None;
        }
    }
}
