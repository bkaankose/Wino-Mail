using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using Wino.Mail.Controls.Primitives;

namespace Wino.Mail.Controls.IntelligenceTileBar;

/// <summary>Renders a wrapping collection of passive mail intelligence indicators.</summary>
public sealed partial class WinoMailIntelligenceTileBar : UserControl
{
    /// <summary>Gets or sets the ordered intelligence tiles.</summary>
    [GeneratedDependencyProperty]
    public partial IEnumerable<WinoIntelligenceTile>? Items { get; set; }

    /// <summary>Gets or sets whether tiles use icon-only rendering.</summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsCompact { get; set; }

    /// <summary>
    /// Gets or sets whether every tile stays on one line. Hosts that must not grow taller as tiles
    /// arrive — a collapsed header, for one — set this and let the tiles trim instead.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSingleRow { get; set; }

    public WinoMailIntelligenceTileBar()
    {
        InitializeComponent();
        UpdatePresentation();

        // The items panels are only realized once the control is loaded, so the wrapping mode has to
        // be reapplied then; setting it in the constructor alone would never reach them.
        Loaded += (_, _) => UpdatePresentation();
    }

    partial void OnItemsChanged(IEnumerable<WinoIntelligenceTile>? newValue) => UpdatePresentation();

    partial void OnIsCompactChanged(bool newValue) => UpdatePresentation();

    partial void OnIsSingleRowChanged(bool newValue) => UpdatePresentation();

    private void UpdatePresentation()
    {
        if (LayoutRoot is null)
            return;

        CompactItemsPanel.Visibility = IsCompact ? Visibility.Visible : Visibility.Collapsed;
        DetailedItemsPanel.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;

        var items = Items?.Where(static item => item is not null).ToArray() ?? [];
        var detailedItems = items.Where(static item => item.Kind == WinoIntelligenceTileKind.SmartLabel).ToArray();
        CompactItemsPanel.ItemsSource = items;
        DetailedItemsPanel.ItemsSource = detailedItems;
        Visibility = (IsCompact ? items : detailedItems).Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyWrappingMode(CompactItemsPanel);
        ApplyWrappingMode(DetailedItemsPanel);
    }

    private void ApplyWrappingMode(ItemsControl itemsControl)
    {
        if (itemsControl.ItemsPanelRoot is WinoWrapPanel panel)
        {
            panel.IsWrappingEnabled = !IsSingleRow;
        }
    }
}
