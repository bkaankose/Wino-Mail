using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels;

namespace Wino.Controls;

public sealed partial class MailInvitationCard : UserControl
{
    [GeneratedDependencyProperty]
    public partial MailInvitationCardViewModel? Invitation { get; set; }

    public MailInvitationCard()
    {
        InitializeComponent();
    }
}
