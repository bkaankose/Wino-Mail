using Wino.Domain.Enums;

namespace Wino.Domain.Models.Personalization
{
    public class ElementThemeContainer
    {
        public ElementThemeContainer(ApplicationElementTheme nativeTheme, string title)
        {
            NativeTheme = nativeTheme;
            Title = title;
        }

        public ApplicationElementTheme NativeTheme { get; set; }
        public string Title { get; set; }
    }
}
