using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Controls;

/// <summary>
/// Chooses what an account is used for. Shared by the account wizard and the IMAP server
/// settings page; the rules live in <see cref="AccountCapabilitySelection"/>.
/// </summary>
public sealed partial class AccountCapabilityPicker : UserControl
{
    [GeneratedDependencyProperty]
    public partial AccountCapabilitySelection? Selection { get; set; }

    /// <summary>
    /// Optional content under the Mail card, such as the wizard's download range.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial object? MailOptionsContent { get; set; }

    /// <summary>
    /// Shows Mail as an expander that hosts <see cref="MailOptionsContent"/>.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial bool ShowMailOptions { get; set; }

    public AccountCapabilityPicker()
    {
        InitializeComponent();
    }
}
