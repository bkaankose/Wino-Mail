namespace Wino.Mail.WinUI.Selectors;

/// <summary>
/// Implemented by template selectors that are declared in page resources. XAML never collects a
/// <see cref="Microsoft.UI.Xaml.Controls.DataTemplateSelector"/> that was created in managed code,
/// so a selector that belongs to a page has to drop its templates when the page goes away.
/// Otherwise every page instance leaves its templates behind.
/// </summary>
public interface IReleasableTemplateSelector
{
    void ReleaseTemplates();
}
