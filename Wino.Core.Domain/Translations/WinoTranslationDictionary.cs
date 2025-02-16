using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Translations
{
    public class WinoTranslationDictionary : Dictionary<string, string>
    {
        // Return the key itself in case of translation is not found.
        public string GetTranslatedString(string key) => TryGetValue(key, out string keyValue) ? keyValue : key;

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
                AppLanguage.Spanish => "es_ES",
                AppLanguage.Indonesian => "id_ID",
                AppLanguage.Italian => "it_IT",
                AppLanguage.Greek => "el_GR",
                AppLanguage.PortugeseBrazil => "pt_BR",
                AppLanguage.Romanian => "ro_RO",
                _ => "en_US",
            };
        }
    }
}
