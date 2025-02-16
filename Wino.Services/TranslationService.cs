using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;
using Wino.Messaging.Client.Shell;

namespace Wino.Services;

public class TranslationService : ITranslationService
{
    public const AppLanguage DefaultAppLanguage = AppLanguage.English;

    private ILogger _logger = Log.ForContext<TranslationService>();
    private readonly IPreferencesService _preferencesService;
    private bool isInitialized = false;

    public AppLanguageModel CurrentLanguageModel { get; private set; }

    public TranslationService(IPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
    }

    // Initialize default language with ignoring current language check.
    public Task InitializeAsync() => InitializeLanguageAsync(_preferencesService.CurrentLanguage, ignoreCurrentLanguageCheck: true);

    public async Task InitializeLanguageAsync(AppLanguage language, bool ignoreCurrentLanguageCheck = false)
    {
        if (!ignoreCurrentLanguageCheck && _preferencesService.CurrentLanguage == language)
        {
            _logger.Warning("Changing language is ignored because current language and requested language are same.");

            return;
        }

        if (ignoreCurrentLanguageCheck && isInitialized) return;

        var currentDictionary = Translator.Resources;
        await using var resourceStream = Core.Domain.Translations.WinoTranslationDictionary.GetLanguageStream(language);

        var streamValue = await new StreamReader(resourceStream).ReadToEndAsync().ConfigureAwait(false);

        var translationLookups = JsonSerializer.Deserialize(streamValue, BasicTypesJsonContext.Default.DictionaryStringString);

        // Insert new translation key-value pairs.
        // Overwrite existing values for the same keys.

        foreach (var pair in translationLookups)
        {
            // Replace existing value.
            currentDictionary[pair.Key] = pair.Value;
        }

        _preferencesService.CurrentLanguage = language;
        CurrentLanguageModel = GetAvailableLanguages().FirstOrDefault(a => a.Language == language);

        isInitialized = true;
        WeakReferenceMessenger.Default.Send(new LanguageChanged());
    }

    public List<AppLanguageModel> GetAvailableLanguages()
    {
        return
        [
            new AppLanguageModel(AppLanguage.Chinese, "Chinese", "zh-CN"),
            new AppLanguageModel(AppLanguage.Czech, "Czech", "cs-CZ"),
            new AppLanguageModel(AppLanguage.Deutsch, "Deutsch", "de-DE"),
            new AppLanguageModel(AppLanguage.English, "English", "en-US"),
            new AppLanguageModel(AppLanguage.French, "French", "fr-FR"),
            new AppLanguageModel(AppLanguage.Italian, "Italian", "it-IT"),
            new AppLanguageModel(AppLanguage.Greek, "Greek", "el-GR"),
            new AppLanguageModel(AppLanguage.Indonesian, "Indonesian", "id-ID"),
            new AppLanguageModel(AppLanguage.Polish, "Polski", "pl-PL"),
            new AppLanguageModel(AppLanguage.PortugeseBrazil, "Portuguese-Brazil", "pt-BR"),
            new AppLanguageModel(AppLanguage.Russian, "Russian", "ru-RU"),
            new AppLanguageModel(AppLanguage.Romanian, "Romanian", "ro-RO"),
            new AppLanguageModel(AppLanguage.Spanish, "Spanish", "es-ES"),
            new AppLanguageModel(AppLanguage.Turkish, "Turkish", "tr-TR")
        ];
    }
}
