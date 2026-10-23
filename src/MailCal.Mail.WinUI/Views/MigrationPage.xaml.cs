using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using MailCal.Mail.WinUI.Interfaces;
using MailCal.Mail.WinUI.ViewModels;

namespace MailCal.Mail.WinUI.Views;

public sealed partial class MigrationPage : Page
{
    private readonly IMailCalDialogService _dialogService;

    public MigrationPageViewModel ViewModel { get; }

    public MigrationPage()
    {
        ViewModel = MailCalApplication.Current.Services.GetRequiredService<MigrationPageViewModel>();
        _dialogService = MailCalApplication.Current.Services.GetRequiredService<IMailCalDialogService>();
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
            MailCal.Core.Domain.Translator.Migration_SkipTitle,
            MailCal.Core.Domain.Translator.Migration_SkipDescription,
            MailCal.Core.Domain.Translator.Migration_SkipConfirm);

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
            Log.Error(ex, "Launching Mail-Cal after migration failed.");
        }
    }

    public Task<bool> ConfirmExitAsync()
        => _dialogService.ShowCautiousConfirmationDialogAsync(
            MailCal.Core.Domain.Translator.Migration_CloseTitle,
            MailCal.Core.Domain.Translator.Migration_CloseDescription,
            MailCal.Core.Domain.Translator.Migration_Exit);
}
