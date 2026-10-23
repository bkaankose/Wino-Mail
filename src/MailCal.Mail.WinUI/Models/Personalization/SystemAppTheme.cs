using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Models.Personalization;

// Mica - Acrylic.
public class SystemAppTheme : PreDefinedAppTheme
{
    public SystemAppTheme(string themeName, Guid id) : base(themeName, id, "") { }

    public override AppThemeType AppThemeType => AppThemeType.System;
}
