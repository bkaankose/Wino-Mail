using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Translations;
using Wino.Messaging.Client.Shell;

namespace Wino.Services
{
    public class TranslationService : ITranslationService
    {
        public const AppLanguage DefaultAppLanguage = AppLanguage.English;

        private ILogger _logger = Log.ForContext<TranslationService>();
        private readonly IPreferencesService _preferencesService;
        private bool isInitialized = false;

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
            using var resourceStream = currentDictionary.GetLanguageStream(language);

            var stremValue = await new StreamReader(resourceStream).ReadToEndAsync().ConfigureAwait(false);

            var translationLookups = JsonSerializer.Deserialize<Dictionary<string, string>>(stremValue);

            // Insert new translation key-value pairs.
            // Overwrite existing values for the same keys.

            foreach (var pair in translationLookups)
            {
                // Replace existing value.
                if (currentDictionary.ContainsKey(pair.Key))
                {
                    currentDictionary[pair.Key] = pair.Value;
                }
                else
                {
                    currentDictionary.Add(pair.Key, pair.Value);
                }
            }

            _preferencesService.CurrentLanguage = language;

            isInitialized = true;
            WeakReferenceMessenger.Default.Send(new LanguageChanged());
        }

        public List<AppLanguageModel> GetAvailableLanguages()
        {
            return
            [
                new AppLanguageModel(AppLanguage.Chinese, "Chinese"),
                new AppLanguageModel(AppLanguage.Czech, "Czech"),
                new AppLanguageModel(AppLanguage.Deutsch, "Deutsch"),
                new AppLanguageModel(AppLanguage.English, "English"),
                new AppLanguageModel(AppLanguage.French, "French"),
                new AppLanguageModel(AppLanguage.Italian, "Italian"),
                new AppLanguageModel(AppLanguage.Greek, "Greek"),
                new AppLanguageModel(AppLanguage.Indonesian, "Indonesian"),
                new AppLanguageModel(AppLanguage.Polish, "Polski"),
                new AppLanguageModel(AppLanguage.PortugeseBrazil, "Portugese-Brazil"),
                new AppLanguageModel(AppLanguage.Russian, "Russian"),
                new AppLanguageModel(AppLanguage.Romanian, "Romanian"),
                new AppLanguageModel(AppLanguage.Spanish, "Spanish"),
                new AppLanguageModel(AppLanguage.Turkish, "Turkish")
            ];
        }
    }
}
