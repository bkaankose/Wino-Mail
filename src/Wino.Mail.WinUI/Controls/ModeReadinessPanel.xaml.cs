using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.ViewModels.Data;

namespace Wino.Controls;

/// <summary>
/// Blocked state for a mode page: why the mode cannot be used yet and what to do about it.
/// </summary>
public sealed partial class ModeReadinessPanel : UserControl
{
    [GeneratedDependencyProperty]
    public partial ModeReadinessViewModel ViewModel { get; set; }

    public ModeReadinessPanel()
    {
        InitializeComponent();
    }
}
