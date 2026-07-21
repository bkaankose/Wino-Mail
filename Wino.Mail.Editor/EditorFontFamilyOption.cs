using Windows.UI.Xaml.Media;

namespace Wino.Mail.Editor;

public sealed class EditorFontFamilyOption
{
    public EditorFontFamilyOption(string displayName)
    {
        DisplayName = displayName;
        try
        {
            PreviewFontFamily = new FontFamily(displayName);
        }
        catch (ArgumentException)
        {
            PreviewFontFamily = new FontFamily("Segoe UI");
        }
    }

    public string DisplayName { get; }
    public FontFamily PreviewFontFamily { get; }
}
