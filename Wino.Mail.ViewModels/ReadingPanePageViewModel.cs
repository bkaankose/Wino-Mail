using System;
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Mail.ViewModels
{
    public partial class ReadingPanePageViewModel : BaseViewModel,
        IRecipient<PropertyChangedMessage<ReaderFontModel>>,
        IRecipient<PropertyChangedMessage<int>>
    {
        public IPreferencesService PreferencesService { get; set; }

        private int selectedMarkAsOptionIndex;
        private readonly IFontService _fontService;

        public int SelectedMarkAsOptionIndex
        {
            get => selectedMarkAsOptionIndex;
            set
            {
                if (SetProperty(ref selectedMarkAsOptionIndex, value))
                {
                    if (value >= 0)
                    {
                        PreferencesService.MarkAsPreference = (MailMarkAsOption)Enum.GetValues(typeof(MailMarkAsOption)).GetValue(value);
                    }
                }
            }
        }

        public List<ReaderFontModel> ReaderFonts => _fontService.GetReaderFonts();

        [ObservableProperty]
        [NotifyPropertyChangedRecipients]
        ReaderFontModel currentReaderFont;

        [ObservableProperty]
        [NotifyPropertyChangedRecipients]
        int currentReaderFontSize;

        public ReadingPanePageViewModel(IDialogService dialogService,
                                        IFontService fontService,
                                        IPreferencesService preferencesService) : base(dialogService)
        {
            _fontService = fontService;

            PreferencesService = preferencesService;
            SelectedMarkAsOptionIndex = Array.IndexOf(Enum.GetValues(typeof(MailMarkAsOption)), PreferencesService.MarkAsPreference);

            CurrentReaderFont = fontService.GetCurrentReaderFont();
            CurrentReaderFontSize = fontService.GetCurrentReaderFontSize();
        }

        public void Receive(PropertyChangedMessage<ReaderFontModel> message)
        {
            if (message.OldValue != message.NewValue)
            {
                _fontService.ChangeReaderFont(message.NewValue.Font);
                Debug.WriteLine("Changed reader font.");
            }
        }

        public void Receive(PropertyChangedMessage<int> message)
        {
            if (message.PropertyName == nameof(CurrentReaderFontSize))
            {
                _fontService.ChangeReaderFontSize(CurrentReaderFontSize);
            }
        }
    }
}
