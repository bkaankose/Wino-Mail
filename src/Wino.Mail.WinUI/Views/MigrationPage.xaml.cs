using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.ViewModels;

namespace Wino.Mail.WinUI.Views;

public sealed partial class MigrationPage : Page
{
    private readonly IWinoDialogService _dialogService;

    public MigrationPageViewModel ViewModel { get; }

    public MigrationPage()
    {
        ViewModel = WinoApplication.Current.Services.GetRequiredService<MigrationPageViewModel>();
        _dialogService = WinoApplication.Current.Services.GetRequiredService<IWinoDialogService>();
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
        var isConfirmed = await _dialogService.ShowCautiousConfirmationDialogAsync(
            Wino.Core.Domain.Translator.Migration_SkipTitle,
            Wino.Core.Domain.Translator.Migration_SkipDescription,
            Wino.Core.Domain.Translator.Migration_SkipConfirm);

        if (isConfirmed)
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

    public Task<bool> ConfirmExitAsync()
        => _dialogService.ShowCautiousConfirmationDialogAsync(
            Wino.Core.Domain.Translator.Migration_CloseTitle,
            Wino.Core.Domain.Translator.Migration_CloseDescription,
            Wino.Core.Domain.Translator.Migration_Exit);
}
