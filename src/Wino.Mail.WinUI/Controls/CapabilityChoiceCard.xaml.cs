using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Controls;

/// <summary>
/// One answer on a capability screen of the account setup wizard.
/// </summary>
public sealed partial class CapabilityChoiceCard : UserControl
{
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Title { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Description { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Glyph { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsSelected { get; set; }

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsRecommended { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? Command { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string CommandParameter { get; set; }

    public CapabilityChoiceCard()
    {
        InitializeComponent();
    }
}
