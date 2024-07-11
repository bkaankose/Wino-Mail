using Wino.Core.Domain.Enums;
#if NET8_0
using Microsoft.UI.Xaml;
#else
using Windows.UI.Xaml;
#endif

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
