using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Translations;

public class WinoTranslationDictionary : Dictionary<string, string>
{
    private readonly Lock _englishGate = new();
    private Dictionary<string, string>? _english;
    private AppLanguage _language = AppLanguage.None;

    /// <summary>
    /// The text of <paramref name="key"/> in the loaded language. A key the language does not have, or has without
    /// text, reads in English; the key itself comes back only when English does not have it either.
    /// </summary>
    public string GetTranslatedString(string key)
    {
        if (TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            return value;

        return GetEnglish() is { } english && english.TryGetValue(key, out var fallback) && !string.IsNullOrEmpty(fallback)
            ? fallback
            : key;
    }

    /// <summary>
    /// Replaces the content with the strings of <paramref name="language"/>. Strings of the language loaded before
    /// do not stay behind, so a key the new language lacks reads in English and not in the previous language.
    /// </summary>
    public void Load(AppLanguage language)
    {
        if (language == _language && Count > 0)
            return;

        var values = ReadLanguage(language)
            ?? throw new InvalidOperationException($"The {language} strings are not embedded.");

        foreach (var staleKey in Keys.Where(key => !values.ContainsKey(key)).ToList())
            Remove(staleKey);

        foreach (var pair in values)
            this[pair.Key] = pair.Value;

        _language = language;
    }

    // English is read a second time only for another language, and only once a key is missing there.
    private Dictionary<string, string>? GetEnglish()
    {
        if (_language == AppLanguage.English)
            return null;

        lock (_englishGate)
        {
            return _english ??= ReadLanguage(AppLanguage.English) ?? [];
        }
    }

    private static Dictionary<string, string>? ReadLanguage(AppLanguage language)
    {
        using var stream = GetLanguageStream(language);

        return stream is null
            ? null
            : JsonSerializer.Deserialize(stream, BasicTypesJsonContext.Default.DictionaryStringString);
    }

    public static Stream GetLanguageStream(AppLanguage language)
    {
        var path = GetLanguageFileNameRelativePath(language);
        var executingAssembly = Assembly.GetExecutingAssembly();

        // Use the assembly's name instead of the module name to construct the resource path
        string assemblyName = executingAssembly.GetName().Name;
        string languageResourcePath = $"{assemblyName}.Translations.{path}.resources.json";
        return executingAssembly.GetManifestResourceStream(languageResourcePath);
    }

    /// <summary>
    /// Returns the relative path of the language file.
    /// All translations are under Translations\{langCode}\resources.json
    /// </summary>
    /// <param name="language">Language</param>
    /// <returns>Relative folder for the language</returns>
    public static string GetLanguageFileNameRelativePath(AppLanguage language)
    {
        return language switch
        {
            AppLanguage.English => "en_US",
            AppLanguage.Turkish => "tr_TR",
            AppLanguage.Deutsch => "de_DE",
            AppLanguage.Russian => "ru_RU",
            AppLanguage.Polish => "pl_PL",
            AppLanguage.Czech => "cs_CZ",
            AppLanguage.French => "fr_FR",
            AppLanguage.Chinese => "zh_CN",
            AppLanguage.ChineseTraditional => "zh_TW",
            AppLanguage.Spanish => "es_ES",
            AppLanguage.Indonesian => "id_ID",
            AppLanguage.Italian => "it_IT",
            AppLanguage.Greek => "el_GR",
            AppLanguage.PortugeseBrazil => "pt_BR",
            AppLanguage.Romanian => "ro_RO",
            AppLanguage.Korean => "ko_KR",
            AppLanguage.Bulgarian => "bg_BG",
            AppLanguage.Catalan => "ca_ES",
            AppLanguage.Danish => "da_DK",
            AppLanguage.Dutch => "nl_NL",
            AppLanguage.Finnish => "fi_FI",
            AppLanguage.Galician => "gl_ES",
            AppLanguage.Hebrew => "he_IL",
            AppLanguage.Japanese => "ja_JP",
            AppLanguage.Lithuanian => "lt_LT",
            AppLanguage.Slovak => "sk_SK",
            AppLanguage.Ukrainian => "uk_UA",
            AppLanguage.Vietnamese => "vi_VN",
            _ => "en_US",
        };
    }
}
