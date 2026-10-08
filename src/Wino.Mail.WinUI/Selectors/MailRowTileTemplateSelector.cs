using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.IntelligenceTileBar;

namespace Wino.Selectors;

/// <summary>
/// Picks the tile visual for a mail row's mixed tile strip, where intelligence metadata and
/// categories share one items control so they wrap and align as a single row of tiles.
/// </summary>
public partial class MailRowTileTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryTemplate { get; set; }
    public DataTemplate? IntelligenceTileTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is WinoIntelligenceTile ? IntelligenceTileTemplate : CategoryTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
