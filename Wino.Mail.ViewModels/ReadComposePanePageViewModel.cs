using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels;

public partial class ReadComposePanePageViewModel : MailBaseViewModel,
    IRecipient<PropertyChangedMessage<string>>,
    IRecipient<PropertyChangedMessage<int>>
{
    private readonly IFontService _fontService;

    public IPreferencesService PreferencesService { get; set; }
    public List<string> AvailableFonts => _fontService.GetFonts();

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    string currentReaderFont;

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    int currentReaderFontSize;

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    string currentComposerFont;

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    int currentComposerFontSize;

    public ReadComposePanePageViewModel(IMailDialogService dialogService,
                                    IFontService fontService,
                                    IPreferencesService preferencesService) 
    {
        _fontService = fontService;
        PreferencesService = preferencesService;

        CurrentReaderFont = preferencesService.ReaderFont;
        CurrentReaderFontSize = preferencesService.ReaderFontSize;

        CurrentComposerFont = preferencesService.ComposerFont;
        CurrentComposerFontSize = preferencesService.ComposerFontSize;
    }

    public void Receive(PropertyChangedMessage<string> message)
    {
        if (message.PropertyName == nameof(CurrentReaderFont) && message.OldValue != message.NewValue)
        {
            PreferencesService.ReaderFont = message.NewValue;
        }

        if (message.PropertyName == nameof(CurrentComposerFont) && message.OldValue != message.NewValue)
        {
            PreferencesService.ComposerFont = message.NewValue;
        }
    }

    public void Receive(PropertyChangedMessage<int> message)
    {
        if (message.PropertyName == nameof(CurrentReaderFontSize))
        {
            PreferencesService.ReaderFontSize = CurrentReaderFontSize;
        }
        else if (message.PropertyName == nameof(CurrentComposerFontSize))
        {
            PreferencesService.ComposerFontSize = CurrentComposerFontSize;
        }
    }
}
