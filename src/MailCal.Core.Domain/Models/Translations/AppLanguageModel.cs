using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Translations;

public record AppLanguageModel(AppLanguage Language, string DisplayName, string Code);
