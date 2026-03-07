using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using EmailValidation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Wino.Core.Domain;
using Wino.Messaging.Client.Shell;
using Wino.Calendar.ViewModels.Data;
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

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _disposables.Add(GetSuggestionBoxDisposable(AttendeeBox));
        _disposables.Add(NotesEditor);

        ViewModel.GetHtmlNotesAsync = async () => await NotesEditor.GetHtmlBodyAsync() ?? string.Empty;
        await NotesEditor.RenderHtmlAsync(" ");
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

    private IDisposable GetSuggestionBoxDisposable(TokenizingTextBox box)
    {
        return Observable.FromEventPattern<TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>, AutoSuggestBoxTextChangedEventArgs>(
                handler => box.TextChanged += handler,
                handler => box.TextChanged -= handler)
            .Throttle(TimeSpan.FromMilliseconds(120))
            .ObserveOn(SynchronizationContext.Current!)
            .Subscribe(async eventPattern =>
            {
                if (eventPattern.EventArgs.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                    return;

                if (eventPattern.Sender is not AutoSuggestBox senderBox || senderBox.Text.Length < 2)
                    return;

                var addresses = await ViewModel.SearchContactsAsync(senderBox.Text).ConfigureAwait(false);
                await ViewModel.ExecuteUIThread(() => senderBox.ItemsSource = addresses);
            });
    }

    private async void TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        if (!EmailValidator.Validate(args.TokenText))
        {
            args.Cancel = true;
            ViewModel.NotifyInvalidEmail(args.TokenText);
            return;
        }

        var deferral = args.GetDeferral();

        try
        {
            var attendee = await ViewModel.GetAttendeeAsync(args.TokenText);
            if (attendee == null)
            {
                args.Cancel = true;
                ViewModel.NotifyAddressExists();
                return;
            }

            args.Item = attendee;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void AddressBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TokenizingTextBox tokenizingTextBox)
            return;

        if (tokenizingTextBox.Items.LastOrDefault() is not ITokenStringContainer info)
            return;

        var currentText = info.Text;
        if (string.IsNullOrWhiteSpace(currentText) || !EmailValidator.Validate(currentText))
            return;

        var attendee = await ViewModel.GetAttendeeAsync(currentText);
        if (attendee == null)
        {
            tokenizingTextBox.Text = string.Empty;
            return;
        }

        ViewModel.AddAttendee(attendee);
        tokenizingTextBox.Text = string.Empty;
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
        }
    }

    private void AttachmentsPane_DragOver(object sender, DragEventArgs e)
    {
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

    private void AttachmentsPane_DragLeave(object sender, DragEventArgs e)
    {
    }

    private async void AttachmentsPane_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
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
