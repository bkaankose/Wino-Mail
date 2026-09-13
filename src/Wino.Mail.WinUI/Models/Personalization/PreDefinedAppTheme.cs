using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;

namespace Wino.Mail.WinUI.Models.Personalization;

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
        Compatibility = forcedElementTheme switch
        {
            ApplicationElementTheme.Dark => ThemeCompatibility.Dark,
            ApplicationElementTheme.Light => ThemeCompatibility.Light,
            _ => ThemeCompatibility.Both
        };
    }

    public override AppThemeType AppThemeType => AppThemeType.PreDefined;

    public override async Task<string> GetThemeResourceDictionaryContentAsync()
    {
        var xamlDictionaryFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri($"ms-appx:///AppThemes/{ThemeName}.xaml"));
        return await FileIO.ReadTextAsync(xamlDictionaryFile);
    }

    protected override async Task<string> GetPreviewImagePathAsync()
    {
        var resourceDictionaryContent = await GetThemeResourceDictionaryContentAsync();
        var resourceDictionary = XDocument.Parse(resourceDictionaryContent);
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        return resourceDictionary.Root?
                   .Elements()
                   .FirstOrDefault(element =>
                       element.Name.LocalName == "String" &&
                       (string?)element.Attribute(xamlNamespace + "Key") == "PreviewImage")?
                   .Value
               ?? string.Empty;
    }
}
