using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Controls;

public sealed partial class AccountIdentityRowControl : UserControl
{
    [GeneratedDependencyProperty]
    public partial MailAccount? Account { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string Description { get; set; }

    public AccountIdentityRowControl()
    {
        InitializeComponent();
    }
}
