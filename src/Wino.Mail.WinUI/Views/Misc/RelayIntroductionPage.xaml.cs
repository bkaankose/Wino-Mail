using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Interfaces;
using Wino.Views.Abstract;

namespace Wino.Views.Misc;

/// <summary>
/// Two cards, one choice. Relay-based sync goes through the privacy policy (a dialog presented by the dialog service);
/// declining it returns to the cards with nothing selected. Timer-based sync, or closing the window, keeps the timer.
/// </summary>
public sealed partial class RelayIntroductionPage : RelayIntroductionPageAbstract
{
    private bool _completed;

    public RelayIntroductionPage()
    {
        InitializeComponent();
        Unloaded += OnPageUnloaded;
    }

    public Brush SelectionBrush(bool selected) => selected
        ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
        : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

    public Thickness SelectionThickness(bool selected) => selected ? new Thickness(2) : new Thickness(1);

    private void TimerCardClicked(object sender, RoutedEventArgs e) => ViewModel.SelectTimer();

    private void RelayCardClicked(object sender, RoutedEventArgs e) => ViewModel.SelectRelay();

    private async void ContinueClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel.IsTimerSelected)
            {
                ViewModel.CompleteWithTimer();
                Close();
                return;
            }

            if (!ViewModel.IsRelaySelected)
                return;

            ErrorText.Visibility = Visibility.Collapsed;

            if (ViewModel.IsPolicyAcceptanceRequired)
            {
                var dialogs = WinoApplication.Current.Services.GetRequiredService<IWinoDialogService>();
                var accepted = await dialogs.ShowRelayPolicyDialogAsync(new Uri(AppUrls.PrivacyPolicy), ViewModel.CompleteWithRelayAsync);
                if (!accepted)
                {
                    // Declined: back to the two cards.
                    ViewModel.ClearSelection();
                    return;
                }
            }
            else if (!await ViewModel.CompleteWithRelayAsync())
            {
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            Close();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The relay introduction could not finish.");
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void Close()
    {
        _completed = true;
        var windowManager = WinoApplication.Current.Services.GetRequiredService<IWinoWindowManager>();
        windowManager.GetWindow(Wino.Mail.WinUI.Models.WinoWindowKind.RelayIntroduction)?.Close();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnPageUnloaded;
        // Closing the window without choosing keeps the timer; the introduction does not come back.
        if (!_completed)
            ViewModel.CompleteWithTimer();
    }
}
