using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MailCal.Selectors;

/// <summary>
/// Picks the tile visual for a mail row's category strip.
/// </summary>
public partial class MailRowTileTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => CategoryTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
