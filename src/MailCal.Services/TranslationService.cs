using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Translations;
using MailCal.Messaging.Client.Shell;

namespace MailCal.Services;

public class TranslationService : ITranslationService
{
    public const AppLanguage DefaultAppLanguage = AppLanguage.English;
    public const string DefaultLanguageCode = "en-US";

    private ILogger _logger = Log.ForContext<TranslationService>();
    private readonly IPreferencesService _preferencesService;
    private readonly IConfigurationService _configurationService;
    private bool isInitialized = false;

    public AppLanguageModel CurrentLanguageModel { get; private set; }

    public TranslationService(IPreferencesService preferencesService, IConfigurationService configurationService)
    {
        _preferencesService = preferencesService;
        _configurationService = configurationService;
    }

    // Initialize default language with ignoring current language check.
    public Task InitializeAsync() => InitializeLanguageAsync(GetInitialLanguage(), ignoreCurrentLanguageCheck: true);

    public async Task InitializeLanguageAsync(AppLanguage language, bool ignoreCurrentLanguageCheck = false)
    {
        if (!ignoreCurrentLanguageCheck && _preferencesService.CurrentLanguage == language)
        {
            _logger.Warning("Changing language is ignored because current language and requested language are same.");

            return;
        }

        if (ignoreCurrentLanguageCheck && isInitialized) return;

        // English is the source language and the only one new strings are added to, so it is the base.
        // The selected language overrides the keys it has; a key it lacks shows in English, not as its name.
        var english = await LoadLanguageAsync(AppLanguage.English).ConfigureAwait(false);
        var selected = language == AppLanguage.English
            ? null
            : await LoadLanguageAsync(language).ConfigureAwait(false);

        var currentDictionary = Translator.Resources;
        currentDictionary.Clear();

        foreach (var pair in english)
            currentDictionary[pair.Key] = pair.Value;

        if (selected != null)
        {
            foreach (var pair in selected)
            {
                if (!string.IsNullOrEmpty(pair.Value))
                    currentDictionary[pair.Key] = pair.Value;
            }
        }

        _preferencesService.CurrentLanguage = language;
        CurrentLanguageModel = GetAvailableLanguages().FirstOrDefault(a => a.Language == language);

        if (CurrentLanguageModel != null)
        {
            var newCulture = new CultureInfo(CurrentLanguageModel.Code);
            CultureInfo.DefaultThreadCurrentCulture = newCulture;
            CultureInfo.DefaultThreadCurrentUICulture = newCulture;
        }

        isInitialized = true;
        WeakReferenceMessenger.Default.Send(new LanguageChanged());
    }

    private static async Task<Dictionary<string, string>> LoadLanguageAsync(AppLanguage language)
    {
        await using var resourceStream = Core.Domain.Translations.MailCalTranslationDictionary.GetLanguageStream(language);
        using var reader = new StreamReader(resourceStream);
        var streamValue = await reader.ReadToEndAsync().ConfigureAwait(false);

        return JsonSerializer.Deserialize(streamValue, BasicTypesJsonContext.Default.DictionaryStringString) ?? [];
    }

    private AppLanguage GetInitialLanguage()
    {
        if (_configurationService.Contains(nameof(IPreferencesService.CurrentLanguage)))
            return _preferencesService.CurrentLanguage;

        var windowsDisplayLanguage = CultureInfo.CurrentUICulture?.Name;
        var initialLanguage = ResolveSupportedLanguage(
        [
            windowsDisplayLanguage ?? string.Empty,
            CultureInfo.CurrentUICulture?.TwoLetterISOLanguageName ?? string.Empty
        ]);

        _logger.Information("No saved app language preference found. Using Windows display language {LanguageTag} -> {Language}.",
            string.IsNullOrWhiteSpace(windowsDisplayLanguage) ? "<unknown>" : windowsDisplayLanguage,
            initialLanguage);

        return initialLanguage;
    }

    internal static AppLanguage ResolveSupportedLanguage(IEnumerable<string> languageTags)
    {
        foreach (var languageTag in languageTags)
        {
            if (string.IsNullOrWhiteSpace(languageTag))
                continue;

            var normalizedLanguageTag = languageTag.Replace('_', '-').Trim();
            var subtags = normalizedLanguageTag.Split('-');
            var languageCode = subtags[0];

            if (string.Equals(languageCode, "zh", System.StringComparison.OrdinalIgnoreCase))
                return IsTraditionalChineseTag(subtags) ? AppLanguage.ChineseTraditional : AppLanguage.Chinese;

            if (TryResolveSupportedLanguage(languageCode, out var supportedLanguage))
                return supportedLanguage;
        }

        return DefaultAppLanguage;
    }

    // zh-Hant, zh-TW, zh-HK and zh-MO use Traditional script. An explicit Hans script wins over the region,
    // and everything else, including bare zh, stays Simplified.
    private static bool IsTraditionalChineseTag(string[] subtags)
    {
        var upperSubtags = subtags.Skip(1).Select(subtag => subtag.ToUpperInvariant()).ToArray();

        if (upperSubtags.Contains("HANS"))
            return false;

        return upperSubtags.Any(subtag => subtag is "HANT" or "TW" or "HK" or "MO");
    }

    private static bool TryResolveSupportedLanguage(string languageCode, out AppLanguage supportedLanguage)
    {
        supportedLanguage = languageCode.ToLowerInvariant() switch
        {
            "bg" => AppLanguage.Bulgarian,
            "ca" => AppLanguage.Catalan,
            "cs" => AppLanguage.Czech,
            "da" => AppLanguage.Danish,
            "de" => AppLanguage.Deutsch,
            "el" => AppLanguage.Greek,
            "en" => AppLanguage.English,
            "es" => AppLanguage.Spanish,
            "fi" => AppLanguage.Finnish,
            "fr" => AppLanguage.French,
            "gl" => AppLanguage.Galician,
            "he" => AppLanguage.Hebrew,
            "id" => AppLanguage.Indonesian,
            "it" => AppLanguage.Italian,
            "ja" => AppLanguage.Japanese,
            "ko" => AppLanguage.Korean,
            "lt" => AppLanguage.Lithuanian,
            "nl" => AppLanguage.Dutch,
            "pl" => AppLanguage.Polish,
            "pt" => AppLanguage.PortugeseBrazil,
            "ro" => AppLanguage.Romanian,
            "ru" => AppLanguage.Russian,
            "sk" => AppLanguage.Slovak,
            "tr" => AppLanguage.Turkish,
            "uk" => AppLanguage.Ukrainian,
            "vi" => AppLanguage.Vietnamese,
            "zh" => AppLanguage.Chinese,
            _ => AppLanguage.None
        };

        return supportedLanguage != AppLanguage.None;
    }

    public List<AppLanguageModel> GetAvailableLanguages() => CreateAvailableLanguages();

    private static List<AppLanguageModel> CreateAvailableLanguages()
        =>
        [
            new AppLanguageModel(AppLanguage.Chinese, "Chinese (Simplified)", "zh-CN"),
            new AppLanguageModel(AppLanguage.ChineseTraditional, "Chinese (Traditional)", "zh-TW"),
            new AppLanguageModel(AppLanguage.Czech, "Czech", "cs-CZ"),
            new AppLanguageModel(AppLanguage.Deutsch, "Deutsch", "de-DE"),
            new AppLanguageModel(AppLanguage.English, "English", "en-US"),
            new AppLanguageModel(AppLanguage.French, "French", "fr-FR"),
            new AppLanguageModel(AppLanguage.Italian, "Italian", "it-IT"),
            new AppLanguageModel(AppLanguage.Greek, "Greek", "el-GR"),
            new AppLanguageModel(AppLanguage.Indonesian, "Indonesian", "id-ID"),
            new AppLanguageModel(AppLanguage.Polish, "Polski", "pl-PL"),
            new AppLanguageModel(AppLanguage.PortugeseBrazil, "Portuguese-Brazil", "pt-BR"),
            new AppLanguageModel(AppLanguage.Korean, "Korean", "ko-KR"),
            new AppLanguageModel(AppLanguage.Russian, "Russian", "ru-RU"),
            new AppLanguageModel(AppLanguage.Romanian, "Romanian", "ro-RO"),
            new AppLanguageModel(AppLanguage.Spanish, "Spanish", "es-ES"),
            new AppLanguageModel(AppLanguage.Turkish, "Turkish", "tr-TR"),
            new AppLanguageModel(AppLanguage.Bulgarian, "Bulgarian", "bg-BG"),
            new AppLanguageModel(AppLanguage.Catalan, "Catalan", "ca-ES"),
            new AppLanguageModel(AppLanguage.Danish, "Danish", "da-DK"),
            new AppLanguageModel(AppLanguage.Dutch, "Dutch", "nl-NL"),
            new AppLanguageModel(AppLanguage.Finnish, "Finnish", "fi-FI"),
            new AppLanguageModel(AppLanguage.Galician, "Galician", "gl-ES"),
            new AppLanguageModel(AppLanguage.Hebrew, "עברית", "he-IL"),
            new AppLanguageModel(AppLanguage.Japanese, "Japanese", "ja-JP"),
            new AppLanguageModel(AppLanguage.Lithuanian, "Lithuanian", "lt-LT"),
            new AppLanguageModel(AppLanguage.Slovak, "Slovak", "sk-SK"),
            new AppLanguageModel(AppLanguage.Ukrainian, "Ukrainian", "uk-UA"),
            new AppLanguageModel(AppLanguage.Vietnamese, "Vietnamese", "vi-VN")
        ];

    public static bool IsSupportedLanguageCode(string? languageCode)
        => CreateAvailableLanguages().Any(language =>
            string.Equals(language.Code, languageCode, System.StringComparison.OrdinalIgnoreCase));
}
