using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Controls;

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
