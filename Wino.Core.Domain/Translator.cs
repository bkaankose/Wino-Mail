using Wino.Core.SourceGeneration.Translator;

namespace Wino.Core.Domain;

/// <summary>
/// Translator class for translation.
/// All translations generated automatically by the source generator.
/// </summary>
[TranslatorGen]
public partial class Translator
{
    public static string GetTranslatedString(string key) => Resources.GetTranslatedString(key);
}
