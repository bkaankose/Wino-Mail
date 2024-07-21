using System;
using System.Threading.Tasks;
using Windows.Storage;
using Wino.Domain.Enums;
using Wino.Domain.Models.Personalization;

namespace Wino.Shared.WinRT.Models.Personalization
{
    /// <summary>
    ///  Forest, Nighty, Clouds etc. applies to pre-defined themes in Wino.
    /// </summary>
    public class PreDefinedAppTheme : AppThemeBase
    {
        public PreDefinedAppTheme(string themeName,
                                  Guid id,
                                  string accentColor = "",
                                  ApplicationElementTheme forcedElementTheme = ApplicationElementTheme.Default) : base(themeName, id)
        {
            AccentColor = accentColor;
            ForceElementTheme = forcedElementTheme;
        }

        public override AppThemeType AppThemeType => AppThemeType.PreDefined;

        public override string GetBackgroundPreviewImagePath()
            => $"ms-appx:///BackgroundImages/{ThemeName}.jpg";

        public override async Task<string> GetThemeResourceDictionaryContentAsync()
        {
            var xamlDictionaryFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///AppThemes/{ThemeName}.xaml"));
            return await FileIO.ReadTextAsync(xamlDictionaryFile);
        }
    }
}
