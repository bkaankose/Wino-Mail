using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Translations;

namespace Wino.Core.Domain.Interfaces
{
    public interface ITranslationService : IInitializeAsync
    {
        Task InitializeLanguageAsync(AppLanguage language, bool ignoreCurrentLanguageCheck = false);
        List<AppLanguageModel> GetAvailableLanguages();
    }
}
