using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using EmailValidation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Calendar;
using Wino.Messaging.Client.Shell;
using Wino.Calendar.ViewModels.Data;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Calendar.Views;

public sealed partial class CalendarEventComposePage : CalendarEventComposePageAbstract,
    IRecipient<ApplicationThemeChanged>
{
    private readonly List<IDisposable> _disposables = [];

    public CalendarEventComposePage()
    {
        InitializeComponent();
    }

    public WinoIconGlyph GetEditorThemeIcon(bool isDarkMode) => isDarkMode ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor;

    public string GetEditorThemeToolTip(bool isDarkMode) => isDarkMode ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void ToggleNotesEditorThemeClicked(object sender, RoutedEventArgs e)
    {
        NotesEditor.ToggleEditorTheme();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _disposables.Add(GetSuggestionBoxDisposable(InviteBox));
        _disposables.Add(NotesEditor);

        ViewModel.GetHtmlNotesAsync = async () => await NotesEditor.GetHtmlBodyAsync() ?? string.Empty;
        var args = e.Parameter as CalendarEventComposeNavigationArgs;

        // Notes use the same spell-check choice as the mail composer.
        await NotesEditor.ConfigureSpellCheckAsync(
            ViewModel.IsComposerSpellCheckEnabled,
            ViewModel.ComposerSpellCheckLanguageCode);
        await NotesEditor.ConfigureAutoCorrectAsync(ViewModel.IsComposerAutoCorrectEnabled);

        await NotesEditor.RenderHtmlAsync(string.IsNullOrWhiteSpace(args?.NotesHtml) ? " " : args.NotesHtml);
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _disposables.Clear();
    }

    private IDisposable GetSuggestionBoxDisposable(AutoSuggestBox box)
    {
        return new SuggestionBoxTextDebouncer(box, TimeSpan.FromMilliseconds(120), async (senderBox, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || senderBox.Text.Length < 2)
            {
                return;
            }

            var addresses = await ViewModel.SearchContactsAsync(senderBox.Text).ConfigureAwait(false);
            await ViewModel.ExecuteUIThread(() => senderBox.ItemsSource = addresses);
        });
    }

    private async void InviteBoxQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is AccountContact contact)
        {
            if (!await TryAddAttendeeAsync(contact.Address, notifyErrors: true))
                return;

            sender.Text = string.Empty;
            sender.ItemsSource = null;
            return;
        }

        await AddTypedAttendeesAsync(sender, notifyErrors: true);
    }

    private async void InviteBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box)
        {
            await AddTypedAttendeesAsync(box, notifyErrors: false);
        }
    }

    // Accepts one address or a pasted list separated by semicolons, commas, or spaces.
    private async Task AddTypedAttendeesAsync(AutoSuggestBox box, bool notifyErrors)
    {
        var addresses = (box.Text ?? string.Empty)
            .Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (addresses.Length == 0)
            return;

        var rejected = new List<string>();

        foreach (var address in addresses)
        {
            if (!await TryAddAttendeeAsync(address, notifyErrors))
            {
                rejected.Add(address);
            }
        }

        // Keep what could not be added so the user can correct it.
        box.Text = string.Join("; ", rejected);
        box.ItemsSource = null;
    }

    private async Task<bool> TryAddAttendeeAsync(string address, bool notifyErrors)
    {
        if (!EmailValidator.Validate(address))
        {
            if (notifyErrors)
                ViewModel.NotifyInvalidEmail(address);

            return false;
        }

        var attendee = await ViewModel.GetAttendeeAsync(address);
        if (attendee == null)
        {
            // The address is already in the list, so there is nothing left to correct.
            if (notifyErrors)
                ViewModel.NotifyAddressExists();

            return true;
        }

        ViewModel.AddAttendee(attendee);
        return true;
    }

    private void ToggleAttendeeOptionalClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CalendarComposeAttendeeViewModel attendee })
        {
            ViewModel.ToggleAttendeeOptionalCommand.Execute(attendee);
        }
    }

    private void RemoveAttendeeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CalendarComposeAttendeeViewModel attendee })
        {
            ViewModel.RemoveAttendeeCommand.Execute(attendee);
        }
    }

    private void RemoveAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CalendarComposeAttachmentViewModel attachment })
        {
            ViewModel.RemoveAttachmentCommand.Execute(attachment);
        }
    }

    private void ComposeCalendarClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AccountCalendarViewModel calendar)
        {
            ViewModel.SelectedCalendar = calendar;
            CalendarSelectionFlyout.Hide();
        }
    }

    private void AttachmentsPane_DragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanAddAttachments)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;

        if (e.AcceptedOperation == DataPackageOperation.Copy)
        {
            e.DragUIOverride.Caption = Translator.ComposerAttachmentsDragDropAttach_Message;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private async void AttachmentsPane_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanAddAttachments || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var storageItems = await e.DataView.GetStorageItemsAsync();
        var files = storageItems.OfType<StorageFile>();

        foreach (var file in files)
        {
            var basicProperties = await file.GetBasicPropertiesAsync();
            await ViewModel.ExecuteUIThread(() => ViewModel.TryAddAttachment(file.Path, (long)basicProperties.Size));
        }
    }

    public void Receive(ApplicationThemeChanged message)
    {
        ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark;
        NotesEditor.IsEditorDarkMode = message.IsUnderlyingThemeDark;
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();
        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
    }
}
