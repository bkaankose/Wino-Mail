using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Common.Data;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Blocked state for a mode page: why the mode cannot be used yet and what to do about it.
/// </summary>
public sealed partial class ModeReadinessPanel : UserControl
{
    [GeneratedDependencyProperty]
    public partial ModeReadinessViewModel? ViewModel { get; set; }

    public ModeReadinessPanel()
    {
        InitializeComponent();
    }
}
