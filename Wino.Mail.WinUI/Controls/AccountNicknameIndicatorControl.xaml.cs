using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Controls;

public sealed partial class AccountNicknameIndicatorControl : UserControl
{
    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string AccountNickname { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string AccountColorHex { get; set; }

    public AccountNicknameIndicatorControl()
    {
        InitializeComponent();
    }
}
