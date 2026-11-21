using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.AccountSettings;

namespace Wino.Mail.WinUI.Controls;

/// <summary>The switch and "Syncs with" blocks that start every app page of an account.</summary>
public sealed partial class AccountAppHeaderControl : UserControl
{
    [GeneratedDependencyProperty]
    public partial AccountAppPageViewModelBase? ViewModel { get; set; }

    public AccountAppHeaderControl()
    {
        InitializeComponent();
    }
}
