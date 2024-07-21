using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Domain.Enums;
using Wino.Domain.Models.Translations;

namespace Wino.Domain.Interfaces
{
    public interface ITranslationService : IInitializeAsync
    {
        Task InitializeLanguageAsync(AppLanguage language, bool ignoreCurrentLanguageCheck = false);
        List<AppLanguageModel> GetAvailableLanguages();
    }
}
