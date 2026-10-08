using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Personalization;

public sealed record ThemeRuntimeState(
    Guid? ThemeId,
    Guid EffectiveThemeId,
    string AccentColor,
    ApplicationElementTheme ElementTheme);
