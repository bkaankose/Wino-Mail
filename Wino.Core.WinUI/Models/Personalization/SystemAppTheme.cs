using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.WinUI.Models.Personalization;

// Mica - Acrylic.
public class SystemAppTheme : PreDefinedAppTheme
{
    public SystemAppTheme(string themeName, Guid id) : base(themeName, id, "") { }

    public override AppThemeType AppThemeType => AppThemeType.System;
}
