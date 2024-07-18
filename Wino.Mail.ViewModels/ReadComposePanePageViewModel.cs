using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public partial class ReadComposePanePageViewModel : BaseViewModel,
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

        public ReadComposePanePageViewModel(IDialogService dialogService,
                                        IFontService fontService,
                                        IPreferencesService preferencesService) : base(dialogService)
        {
            _fontService = fontService;
            PreferencesService = preferencesService;

            CurrentReaderFont = fontService.GetCurrentReaderFont();
            CurrentReaderFontSize = fontService.GetCurrentReaderFontSize();

            CurrentComposerFont = fontService.GetCurrentComposerFont();
            CurrentComposerFontSize = fontService.GetCurrentComposerFontSize();
        }

        public void Receive(PropertyChangedMessage<string> message)
        {
            if (message.PropertyName == nameof(CurrentReaderFont) && message.OldValue != message.NewValue)
            {
                _fontService.SetReaderFont(message.NewValue);
            }

            if (message.PropertyName == nameof(CurrentComposerFont) && message.OldValue != message.NewValue)
            {
                _fontService.SetComposerFont(message.NewValue);
            }
        }

        public void Receive(PropertyChangedMessage<int> message)
        {
            if (message.PropertyName == nameof(CurrentReaderFontSize))
            {
                _fontService.SetReaderFontSize(CurrentReaderFontSize);
            }
            else if (message.PropertyName == nameof(CurrentComposerFontSize))
            {
                _fontService.SetComposerFontSize(CurrentComposerFontSize);
            }
        }
    }
}
