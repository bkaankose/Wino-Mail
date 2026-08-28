using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Controls;

/// <summary>
/// Header row of a contact editor section: icon tile, title, one-line description and an optional entry count.
/// </summary>
public sealed partial class ContactSectionHeader : UserControl
{
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Glyph { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Title { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Description { get; set; }

    /// <summary>Zero hides the badge entirely.</summary>
    [GeneratedDependencyProperty(DefaultValue = 0)]
    public partial int ItemCount { get; set; }

    public ContactSectionHeader()
    {
        InitializeComponent();
    }
}
