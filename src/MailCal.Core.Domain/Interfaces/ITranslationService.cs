using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Translations;

namespace MailCal.Core.Domain.Interfaces;

public interface ITranslationService : IInitializeAsync
{
    Task InitializeLanguageAsync(AppLanguage language, bool ignoreCurrentLanguageCheck = false);
    List<AppLanguageModel> GetAvailableLanguages();
    AppLanguageModel CurrentLanguageModel { get; }
}
