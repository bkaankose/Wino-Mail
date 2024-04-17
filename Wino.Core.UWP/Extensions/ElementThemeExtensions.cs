using Windows.UI.Xaml;
using Wino.Core.Domain.Enums;

namespace Wino.Core.UWP.Extensions
{
    public static class ElementThemeExtensions
    {
        public static ApplicationElementTheme ToWinoElementTheme(this ElementTheme elementTheme)
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
}
