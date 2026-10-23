using Microsoft.UI.Xaml;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Extensions;

public static class ElementThemeExtensions
{
    public static ApplicationElementTheme ToMailCalElementTheme(this ElementTheme elementTheme)
    {
        switch (elementTheme)
        {
            case ElementTheme.Light:
                return ApplicationElementTheme.Light;
            case ElementTheme.Dark:
                return ApplicationElementTheme.Dark;
        }

        return ApplicationElementTheme.Default;
    }

    public static ElementTheme ToWindowsElementTheme(this ApplicationElementTheme elementTheme)
    {
        switch (elementTheme)
        {
            case ApplicationElementTheme.Light:
                return ElementTheme.Light;
            case ApplicationElementTheme.Dark:
                return ElementTheme.Dark;
        }

        return ElementTheme.Default;
    }
}
