using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Personalization;

public sealed record ThemeRuntimeState(
    Guid? ThemeId,
    Guid EffectiveThemeId,
    string AccentColor,
    ApplicationElementTheme ElementTheme);
