using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Browsers;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;
using Wino.Core.Services;
using Wino.Core.Domain.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels;

public partial class ReadComposePanePageViewModel : MailBaseViewModel
{
    public IPreferencesService PreferencesService { get; set; }
    public List<string> AvailableFonts => FontService.GetFonts();
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

    private static readonly MailContentTheme[] ReaderContentThemes =
        [MailContentTheme.FollowApplication, MailContentTheme.Light, MailContentTheme.Dark];
    private static readonly MailDarkModeMethod[] ReaderDarkModeMethods =
        [MailDarkModeMethod.Adaptive, MailDarkModeMethod.Inverted];

    public List<string> ReaderContentThemeOptions { get; } =
    [
        Translator.SettingsReaderMessageColors_FollowApplication,
        Translator.SettingsReaderMessageColors_AlwaysLight,
        Translator.SettingsReaderMessageColors_AlwaysDark
    ];

    [ObservableProperty]
    public partial int SelectedReaderContentThemeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedReaderDarkModeMethodIndex { get; set; }

    public ReadComposePanePageViewModel(IMessenger messenger, IMailDialogService dialogService,
                                    IPreferencesService preferencesService,
                                    ITranslationService translationService,
                                    IEnumerable<ISpellCheckLanguageSource> spellCheckLanguageSources) : base(messenger)
    {
        PreferencesService = preferencesService;

        var spellCheckSource = spellCheckLanguageSources.FirstOrDefault();
        var savedSpellCheckCode = preferencesService.ComposerSpellCheckLanguageCode;
        var platformLanguages = spellCheckSource?.GetLanguages() ?? Array.Empty<(string Code, string DisplayName)>();

        // Spell-check languages are not app languages; the menu reuses the app language model for every head's binding,
        // so the enum value of a platform language means nothing and only the code and the name are read.
        AvailableSpellCheckLanguages = platformLanguages.Count > 0
            ? [.. platformLanguages.Select(language => new AppLanguageModel(AppLanguage.None, language.DisplayName, language.Code))]
            : translationService.GetAvailableLanguages();

        var selectedCode = spellCheckSource is not null && platformLanguages.Count > 0
            ? spellCheckSource.Resolve(savedSpellCheckCode)
            : savedSpellCheckCode;

        CurrentReaderFont = preferencesService.ReaderFont;
        CurrentReaderFontSize = preferencesService.ReaderFontSize;

        CurrentComposerFont = preferencesService.ComposerFont;
        CurrentComposerFontSize = preferencesService.ComposerFontSize;
        CurrentSpellCheckLanguage = AvailableSpellCheckLanguages.Find(language =>
            string.Equals(language.Code, selectedCode, System.StringComparison.OrdinalIgnoreCase));

        SelectedReaderContentThemeIndex = Math.Max(0, Array.IndexOf(ReaderContentThemes, preferencesService.ReaderContentTheme));
        SelectedReaderDarkModeMethodIndex = Math.Max(0, Array.IndexOf(ReaderDarkModeMethods, preferencesService.ReaderDarkModeMethod));
    }

    partial void OnSelectedReaderContentThemeIndexChanged(int value)
    {
        if (value < 0 || value >= ReaderContentThemes.Length) return;

        if (PreferencesService.ReaderContentTheme != ReaderContentThemes[value])
            PreferencesService.ReaderContentTheme = ReaderContentThemes[value];
    }

    partial void OnSelectedReaderDarkModeMethodIndexChanged(int value)
    {
        if (value < 0 || value >= ReaderDarkModeMethods.Length) return;

        if (PreferencesService.ReaderDarkModeMethod != ReaderDarkModeMethods[value])
            PreferencesService.ReaderDarkModeMethod = ReaderDarkModeMethods[value];
    }

    [RelayCommand]
    private void OpenReaderActions()
        => Messenger.Send(new BreadcrumbNavigationRequested(Translator.ReaderActions_Title, WinoPage.ReaderActionsPage));

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
