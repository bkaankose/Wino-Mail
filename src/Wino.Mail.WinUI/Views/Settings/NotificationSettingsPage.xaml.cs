using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels.Data;
using Wino.Helpers;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class NotificationSettingsPage : NotificationSettingsPageAbstract
{
    /// <summary>
    /// How often the page re-reads the snooze state. Snooze expiry is evaluated lazily rather than
    /// scheduled, so this only keeps the countdown text and the toggle honest while the page is open.
    /// </summary>
    private static readonly TimeSpan SnoozeRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DispatcherQueueTimer _snoozeRefreshTimer;

    public NotificationSettingsPage()
    {
        InitializeComponent();

        _snoozeRefreshTimer = DispatcherQueue.CreateTimer();
        _snoozeRefreshTimer.Interval = SnoozeRefreshInterval;
        _snoozeRefreshTimer.Tick += SnoozeRefreshTimer_Tick;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e) => _snoozeRefreshTimer.Start();

    private void Page_Unloaded(object sender, RoutedEventArgs e) => _snoozeRefreshTimer.Stop();

    private void SnoozeRefreshTimer_Tick(DispatcherQueueTimer sender, object args) => ViewModel.RefreshSnoozeState();

    private void SnoozeToggle_IsCheckedChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
    {
        // The event also fires when the binding pushes a new value in, so only act on a real divergence.
        if (sender.IsChecked == ViewModel.IsSnoozed)
            return;

        if (sender.IsChecked)
        {
            ViewModel.StartSnoozeCommand.Execute(ViewModel.SelectedSnoozePreset?.Value ?? NotificationSnoozePreset.UntilTurnedBackOn);
        }
        else
        {
            ViewModel.EndSnoozeCommand.Execute(null);
        }
    }

    private void SnoozePreset_Click(object? sender, EventArgs e)
    {
        if (sender is WinoContextFlyoutItem { CommandParameter: string value } &&
            Enum.TryParse<NotificationSnoozePreset>(value, out var preset))
        {
            ViewModel.StartSnoozeCommand.Execute(preset);
        }
    }

    private void PlayMailNotificationSoundButton_Click(object sender, RoutedEventArgs e)
        => NotificationSoundPlayer.Play(ViewModel.SelectedMailSoundEvent);

    private void PlayCalendarNotificationSoundButton_Click(object sender, RoutedEventArgs e)
        => NotificationSoundPlayer.Play(ViewModel.SelectedCalendarSoundEvent);

    private void PlayTaskNotificationSoundButton_Click(object sender, RoutedEventArgs e)
        => NotificationSoundPlayer.Play(ViewModel.SelectedTaskSoundEvent);

    private void PlayAccountNotificationSoundButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountNotificationSettingsViewModel account })
        {
            NotificationSoundPlayer.Play(account.SelectedSoundEvent);
        }
    }

    private void QuietHoursChanged(object sender, RoutedEventArgs e) => ViewModel.UpdateQuietHoursSummary();

    private void QuietHoursTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) => ViewModel.UpdateQuietHoursSummary();

    /// <summary>
    /// Each section's one-line summary says whether that type is off, so it has to be rebuilt when
    /// the header switch changes. The switch itself binds straight to the preference.
    /// </summary>
    private void SectionToggled(object sender, RoutedEventArgs e) => ViewModel.UpdateSectionSummaries();

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Translator.NotificationSettings_Reset_ConfirmTitle,
            Content = Translator.NotificationSettings_Reset_ConfirmMessage,
            PrimaryButtonText = Translator.NotificationSettings_Reset_Title,
            CloseButtonText = Translator.Buttons_Cancel,
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ResetAsync();
        }
    }
}
