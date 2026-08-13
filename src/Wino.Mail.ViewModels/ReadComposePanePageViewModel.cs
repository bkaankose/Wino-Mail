using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels;

public partial class ReadComposePanePageViewModel : MailBaseViewModel
{
    private readonly IFontService _fontService;

    public IPreferencesService PreferencesService { get; set; }
    public List<string> AvailableFonts => _fontService.GetFonts();

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    public partial string CurrentReaderFont { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    public partial int CurrentReaderFontSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    public partial string CurrentComposerFont { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    public partial int CurrentComposerFontSize { get; set; }

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

    partial void OnCurrentReaderFontChanged(string value)
    {
        if (PreferencesService.ReaderFont != value)
        {
            PreferencesService.ReaderFont = value;
        }
    }

    partial void OnCurrentReaderFontSizeChanged(int value)
    {
        if (PreferencesService.ReaderFontSize != value)
        {
            PreferencesService.ReaderFontSize = value;
        }
    }

    partial void OnCurrentComposerFontChanged(string value)
    {
        if (PreferencesService.ComposerFont != value)
        {
            PreferencesService.ComposerFont = value;
        }
    }

    partial void OnCurrentComposerFontSizeChanged(int value)
    {
        if (PreferencesService.ComposerFontSize != value)
        {
            PreferencesService.ComposerFontSize = value;
        }
    }
}
