#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels.ActionCards;

namespace Wino.Mail.ViewModels;

public partial class MailRenderingPageViewModel
{
    /// <summary>The cards built from the structured data of the shown message, in the sender's order.</summary>
    public ObservableCollection<ActionCardModel> ActionCards { get; } = [];

    /// <summary>The state every card opens in: the one the user last left a card in.</summary>
    [ObservableProperty]
    public partial bool AreActionCardsExpanded { get; set; }

    /// <summary>
    /// Shows what the reader found after drawing the message. Cards appear only when the user wants them and the message
    /// is not an invitation, whose own card already shows the event.
    /// </summary>
    public void ShowInsights(MailReaderInsights insights)
    {
        ArgumentNullException.ThrowIfNull(insights);

        ActionCards.Clear();

        if (!PreferencesService.IsActionCardsEnabled || Invitation.IsVisible || insights.StructuredData.Count == 0)
            return;

        AreActionCardsExpanded = PreferencesService.IsActionCardExpanded;

        foreach (var card in ActionCardBuilder.Build(insights.StructuredData, SenderDisplayName))
        {
            card.IsExpanded = AreActionCardsExpanded;
            ActionCards.Add(card);
        }
    }

    partial void OnAreActionCardsExpandedChanged(bool value)
    {
        if (ActionCards.Count > 0 && PreferencesService.IsActionCardExpanded != value)
            PreferencesService.IsActionCardExpanded = value;
    }

    private string SenderDisplayName => string.IsNullOrWhiteSpace(FromName) ? FromAddress ?? string.Empty : FromName;

    [RelayCommand]
    private async Task InvokeActionCardActionAsync(ActionCardAction? action)
    {
        if (action is null)
            return;

        try
        {
            switch (action.Kind)
            {
                case ActionCardActionKind.OpenSenderLink when action.Address is { } address:
                    await OpenActionCardLinkAsync(action.Label, address);
                    break;
                case ActionCardActionKind.AddToCalendar when initializedMailItemViewModel?.MailCopy is { } mail && _connectedItems is not null:
                    await _connectedItems.CreateEventFromMailAsync(mail, action.Start, action.End, action.IsAllDay, action.ItemTitle, action.Location);
                    break;
                case ActionCardActionKind.CreateTask when initializedMailItemViewModel?.MailCopy is { } mail && _connectedItems is not null:
                    await _connectedItems.CreateTaskFromMailAsync(mail, action.Start, action.ItemTitle);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "An action card action failed.");
            _dialogService.InfoBarMessage(Translator.Info_RequestCreationFailedTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    // The card's own question replaces the general link confirmation: the user has just been shown the address.
    private async Task OpenActionCardLinkAsync(string label, Uri address)
    {
        if (PreferencesService.IsActionCardConfirmationEnabled)
        {
            var answer = await _dialogService.ShowActionCardOpenConfirmationAsync(label, SenderDisplayName, address);

            if (answer.DontAskAgain && answer.IsConfirmed)
            {
                PreferencesService.IsActionCardConfirmationEnabled = false;
                _dialogService.InfoBarMessage(Translator.SettingsRendering_ActionCardConfirmation_Title, Translator.ActionCardOpenDialog_ChangedNote, InfoBarMessageType.Information);
            }

            if (!answer.IsConfirmed)
                return;
        }

        var target = ShouldStripLinkTrackingParameters ? _linkSanitizer.Clean(address, out _) : address;

        (await (_linkBrowserService?.OpenLinkAsync(target, CurrentMailAccountId) ?? ExternalLauncher.LaunchUriAsync(target))).ThrowIfNotSucceeded();
    }

    /// <summary>Opens a new event on a date found in the message, with the subject as its title.</summary>
    [RelayCommand]
    private Task AddDateToCalendarAsync(MailDetectedDate? date)
    {
        if (date is null || initializedMailItemViewModel?.MailCopy is not { } mail || _connectedItems is null)
            return Task.CompletedTask;

        var start = date.Date.ToDateTime(date.Time ?? TimeOnly.MinValue);

        return _connectedItems.CreateEventFromMailAsync(mail, start, date.Time is null ? start.AddDays(1) : start.AddHours(1), date.Time is null);
    }

    /// <summary>Creates a task due on a date found in the message.</summary>
    [RelayCommand]
    private Task CreateTaskForDateAsync(MailDetectedDate? date)
    {
        if (date is null || initializedMailItemViewModel?.MailCopy is not { } mail || _connectedItems is null)
            return Task.CompletedTask;

        return _connectedItems.CreateTaskFromMailAsync(mail, date.Date.ToDateTime(TimeOnly.MinValue));
    }

    /// <summary>A date as the user's culture writes it, for the date menu and for copying.</summary>
    public static string FormatDetectedDate(MailDetectedDate date)
    {
        ArgumentNullException.ThrowIfNull(date);

        var value = date.Date.ToDateTime(date.Time ?? TimeOnly.MinValue);
        var text = date.Time is null
            ? value.ToString("D", CultureInfo.CurrentCulture)
            : $"{value.ToString("D", CultureInfo.CurrentCulture)}, {value.ToString("t", CultureInfo.CurrentCulture)}";

        return date.HasYear ? text : string.Format(CultureInfo.CurrentCulture, Translator.Reader_Date_NoYearFormat, text);
    }
}
