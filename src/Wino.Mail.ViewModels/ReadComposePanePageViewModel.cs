using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;

namespace Wino.Mail.ViewModels;

public partial class ReadComposePanePageViewModel : MailBaseViewModel
{
    private readonly IFontService _fontService;

    public IPreferencesService PreferencesService { get; set; }
    public List<string> AvailableFonts => _fontService.GetFonts();
    public List<AppLanguageModel> AvailableSpellCheckLanguages { get; }

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

    [ObservableProperty]
    public partial AppLanguageModel? CurrentSpellCheckLanguage { get; set; }

    public ReadComposePanePageViewModel(IMailDialogService dialogService,
                                    IFontService fontService,
                                    IPreferencesService preferencesService,
                                    ITranslationService translationService)
    {
        _fontService = fontService;
        PreferencesService = preferencesService;
        AvailableSpellCheckLanguages = translationService.GetAvailableLanguages();

        CurrentReaderFont = preferencesService.ReaderFont;
        CurrentReaderFontSize = preferencesService.ReaderFontSize;

        CurrentComposerFont = preferencesService.ComposerFont;
        CurrentComposerFontSize = preferencesService.ComposerFontSize;
        CurrentSpellCheckLanguage = AvailableSpellCheckLanguages.Find(language =>
            string.Equals(language.Code, preferencesService.ComposerSpellCheckLanguageCode, System.StringComparison.OrdinalIgnoreCase));
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

    partial void OnCurrentSpellCheckLanguageChanged(AppLanguageModel? value)
    {
        if (value != null && PreferencesService.ComposerSpellCheckLanguageCode != value.Code)
        {
            PreferencesService.ComposerSpellCheckLanguageCode = value.Code;
        }
    }
}
