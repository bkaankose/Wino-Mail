using System;
using Wino.Domain.Enums;

namespace Wino.Shared.WinRT.Models.Personalization
{
    // Mica - Acrylic.
    public class SystemAppTheme : PreDefinedAppTheme
    {
        public SystemAppTheme(string themeName, Guid id) : base(themeName, id, "") { }

        public override AppThemeType AppThemeType => AppThemeType.System;
    }
}
