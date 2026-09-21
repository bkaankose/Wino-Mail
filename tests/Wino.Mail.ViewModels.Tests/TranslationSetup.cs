using System.Runtime.CompilerServices;
using System.Text.Json;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Translations;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// View models read their text through <see cref="Translator"/>, whose dictionary stays empty
/// until a language is loaded. Nothing starts the app here, so English is loaded once for the
/// whole assembly; without it every label under test is its resource key rather than the string
/// a user would read, and a test asserting real wording can never pass.
/// </summary>
internal static class TranslationSetup
{
    [ModuleInitializer]
    internal static void LoadEnglish()
    {
        using var stream = WinoTranslationDictionary.GetLanguageStream(AppLanguage.English);

        if (stream is null) return;

        var translations = JsonSerializer.Deserialize(stream, BasicTypesJsonContext.Default.DictionaryStringString);

        if (translations is null) return;

        foreach (var translation in translations)
        {
            Translator.Resources[translation.Key] = translation.Value;
        }
    }
}
